using System.Text;
using System.Threading.Channels;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed partial class MultiAgentRuntime
{
    private sealed class ModelEvent(string agent, string round, Dictionary<string, object?> data)
    {
        public string Agent { get; } = agent;
        public string Round { get; } = round;
        public Dictionary<string, object?> Data { get; } = data;
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class LiveItem
    {
        public required string Agent { get; init; }
        public required string Round { get; init; }
        public required Dictionary<string, object?> Item { get; init; }
        public int Index { get; init; }
        public bool Hidden { get; init; }
        public bool Finished { get; set; }
        public string CallId => Text(Item, "call_id");
        public StringBuilder CustomInput { get; } = new();
        public string SentCustomInput { get; set; } = "";
    }
    private readonly Channel<ModelEvent> _modelEvents = Channel.CreateBounded<ModelEvent>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<(string Round, int Index), LiveItem> _liveItems = [];

    private async Task EnqueueModelEvent(string agent, string round, Dictionary<string, object?> data, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var item = new ModelEvent(agent, round, WebSearchPayload.DeepCopyObject(data));
        await _modelEvents.Writer.WriteAsync(item, ct);
        await item.Accepted.Task.WaitAsync(ct);
    }

    private LiveItem? FindLiveItem(string round, int sourceIndex) => _liveItems.GetValueOrDefault((round, sourceIndex));

    private async Task ProcessModelEvent(ModelEvent incoming)
    {
        try
        {
            if (!_active.TryGetValue(incoming.Agent, out var operation) || operation.Round != incoming.Round || operation.Stop.IsCancellationRequested)
                return;
            var data = incoming.Data;
            var type = Text(data, "type");
            if (type is "response.created" or "response.in_progress" or "response.completed" or "response.failed" or "response.incomplete" or "error") return;
            var sourceIndex = WebSearchPayload.ToInt(JsonDictionaryValue.Get(data, "output_index"), 0);
            if (type == "response.output_item.added")
            {
                var source = JsonDictionaryValue.Object(data, "item", WebSearchPayload.DeepCopyObject);
                var itemType = Text(source, "type");
                var hidden = itemType == "function_call" && MultiAgentProtocol.ActionName(Text(source, "name")) is not null;
                source["id"] = Id(itemType == "message" ? "msg" : "item");
                source["agent"] = new Dictionary<string, object?> { ["agent_name"] = incoming.Agent };
                if (itemType == "message") source.Remove("phase");
                if (itemType is "function_call" or "custom_tool_call") source["call_id"] = Id("call_ma");
                var live = new LiveItem { Agent = incoming.Agent, Round = incoming.Round, Item = source, Index = _output.Count, Hidden = hidden };
                _liveItems.Add((incoming.Round, sourceIndex), live);
                if (hidden) return;
                _output.Add(source);
                await Event(type, ("output_index", live.Index), ("item", WebSearchPayload.DeepCopyObject(source)), ("agent", source["agent"]));
                return;
            }

            var state = FindLiveItem(incoming.Round, sourceIndex);
            if (state is null || state.Hidden || state.Finished) return;
            // Done is committed only after the coordinator has accepted the complete model call.
            if (type == "response.output_item.done") return;
            var forwarded = WebSearchPayload.DeepCopyObject(data);
            forwarded["output_index"] = state.Index;
            forwarded["item_id"] = state.Item["id"];
            forwarded["agent"] = state.Item["agent"];
            if (type == "response.content_part.added" && JsonDictionaryValue.Get(data, "part") is Dictionary<string, object?> part)
                JsonDictionaryValue.List(state.Item, "content").Add(WebSearchPayload.DeepCopyObject(part));
            if (type == "response.output_text.delta")
            {
                var contents = JsonDictionaryValue.List(state.Item, "content");
                var n = WebSearchPayload.ToInt(JsonDictionaryValue.Get(data, "content_index"), 0);
                while (contents.Count <= n) contents.Add(new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "", ["annotations"] = new List<object?>() });
                var textPart = (Dictionary<string, object?>)contents[n]!;
                textPart["text"] = (JsonDictionaryValue.Get(textPart, "text")?.ToString() ?? "") + JsonDictionaryValue.Get(data, "delta");
                state.Item["content"] = contents;
            }
            if (type == "response.function_call_arguments.delta")
                state.Item["arguments"] = (JsonDictionaryValue.Get(state.Item, "arguments")?.ToString() ?? "") + JsonDictionaryValue.Get(data, "delta");
            if (type == "response.custom_tool_call_input.delta")
            {
                state.CustomInput.Append(JsonDictionaryValue.Get(data, "delta"));
                var decoded = DecodeCustomInput(state.CustomInput.ToString());
                var delta = decoded[state.SentCustomInput.Length..];
                state.SentCustomInput = decoded;
                state.Item["input"] = decoded;
                if (delta.Length == 0) return;
                forwarded["delta"] = delta;
            }
            if (type == "response.custom_tool_call_input.done")
            {
                var raw = JsonDictionaryValue.Get(data, "input")?.ToString() ?? state.CustomInput.ToString();
                var complete = DecodeCustomInput(raw, complete: true);
                if (complete.Length > state.SentCustomInput.Length)
                    await Event("response.custom_tool_call_input.delta", ("output_index", state.Index), ("item_id", state.Item["id"]),
                        ("agent", state.Item["agent"]), ("delta", complete[state.SentCustomInput.Length..]));
                state.SentCustomInput = complete;
                state.Item["input"] = complete;
                forwarded["input"] = complete;
            }
            forwarded.Remove("sequence_number");
            forwarded.Remove("type");
            await Event(type, forwarded.Select(p => (p.Key, p.Value)).ToArray());
        }
        catch (Exception error)
        {
            incoming.Accepted.TrySetException(error);
            throw;
        }
        finally { incoming.Accepted.TrySetResult(); }
    }

    private async Task FinishModelItem(string agent, string round, int sourceIndex, Dictionary<string, object?> source)
    {
        var live = FindLiveItem(round, sourceIndex);
        if (live is null) { await Item(agent, source); return; }
        if (live.Hidden || live.Finished) return;
        var id = live.Item["id"];
        var attribution = live.Item["agent"];
        live.Item.Clear();
        foreach (var p in WebSearchPayload.DeepCopyObject(source)) live.Item[p.Key] = p.Value;
        live.Item["id"] = id;
        live.Item["agent"] = attribution;
        live.Finished = true;
        _run.OutputHistory.Add(WebSearchPayload.DeepCopyObject(live.Item));
        await Event("response.output_item.done", ("output_index", live.Index), ("item", WebSearchPayload.DeepCopyObject(live.Item)), ("agent", attribution));
    }

    private async Task CloseModelItems(string? round = null)
    {
        foreach (var state in _liveItems.Values.Where(s => !s.Hidden && !s.Finished && (round is null || s.Round == round)))
        {
            state.Finished = true;
            state.Item["status"] = "incomplete";
            if (Text(state.Item, "type") is "function_call" or "custom_tool_call") continue;
            state.Item["phase"] = "commentary";
            await Event("response.output_item.done", ("output_index", state.Index), ("item", WebSearchPayload.DeepCopyObject(state.Item)), ("agent", state.Item["agent"]));
        }
    }

    // Chat represents custom input as one JSON string. Decode only complete characters,
    // retaining an unfinished escape until its next network fragment arrives.
    private static string DecodeCustomInput(string input, bool complete = false)
    {
        if (!input.TrimStart().StartsWith('{')) return input;
        var offset = 0;
        foreach (var token in new[] { "{", "\"input\"", ":", "\"" })
        {
            while (offset < input.Length && char.IsWhiteSpace(input[offset])) offset++;
            foreach (var expected in token)
            {
                if (offset == input.Length) return complete ? input : "";
                if (input[offset++] != expected) return input;
            }
        }
        var decoded = new StringBuilder();
        for (var i = offset; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"') break;
            if (c != '\\') { decoded.Append(c); continue; }
            if (++i == input.Length) break;
            var escape = input[i];
            if (escape == 'u')
            {
                if (i + 4 >= input.Length) break;
                decoded.Append((char)Convert.ToInt32(input.Substring(i + 1, 4), 16));
                i += 4;
            }
            else decoded.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => escape });
        }
        if (decoded.Length > 0 && char.IsHighSurrogate(decoded[^1])) decoded.Length--;
        return decoded.ToString();
    }
}
