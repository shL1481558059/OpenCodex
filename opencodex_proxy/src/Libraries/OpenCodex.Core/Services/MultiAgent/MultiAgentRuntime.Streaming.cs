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
        public required MultiAgentDeltaAccumulator<(Dictionary<string,object?> Target,string Field)> Deltas { get; init; }
        public string CallId => Text(Item, "call_id");
    }
    private readonly Channel<ModelEvent> _modelEvents = Channel.CreateBounded<ModelEvent>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<(string Round, int Index), LiveItem> _liveItems = [];

    // Unlike identity fields, wire text/arguments must not trim whitespace.
    private static string Raw(IReadOnlyDictionary<string,object?> value,string field) =>
        JsonDictionaryValue.Get(value,field)?.ToString() ?? "";

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
                source["agent"] = new Dictionary<string, object?> { ["agent_name"] = _client?.AgentName ?? incoming.Agent };
                // Commentary is authoritative. A final phase is committed only after
                // accepting the whole model response and checking unfinished descendants.
                if (itemType == "message" && Text(source, "phase") != "commentary") source.Remove("phase");
                if (itemType is "function_call" or "custom_tool_call") source["call_id"] = Id("call_ma");
                var live = new LiveItem { Agent = incoming.Agent, Round = incoming.Round, Item = source, Index = _output.Count, Hidden = hidden, Deltas = new(_deltaBudget) };
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
                if(n < 0 || n >= _maxDeltaFields) throw new OpenCodex.Core.Errors.UpstreamException("The multi-agent content index exceeds its delta field budget.");
                while (contents.Count <= n) contents.Add(new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "", ["annotations"] = new List<object?>() });
                var textPart = (Dictionary<string, object?>)contents[n]!;
                state.Deltas.Append((textPart,"text"),Raw(data,"delta"),Raw(textPart,"text"));
                state.Item["content"] = contents;
            }
            if (type == "response.function_call_arguments.delta")
                state.Deltas.Append((state.Item,"arguments"),Raw(data,"delta"),Raw(state.Item,"arguments"));
            if (type == "response.custom_tool_call_input.delta")
                state.Deltas.Append((state.Item,"input"),Raw(data,"delta"),Raw(state.Item,"input"));
            if (type == "response.custom_tool_call_input.done")
            {
                var complete = JsonDictionaryValue.Get(data, "input")?.ToString()
                    ?? state.Deltas.Take((state.Item,"input")) ?? Raw(state.Item,"input");
                state.Deltas.Discard((state.Item,"input"));
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
        // Accepted terminal item remains authoritative; discard speculative builders without
        // creating a second complete string just before replacing it.
        live.Deltas.Clear();
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
            foreach(var key in state.Deltas.Keys.ToArray()) key.Target[key.Field]=state.Deltas.Take(key);
            state.Finished = true;
            state.Item["status"] = "incomplete";
            if (Text(state.Item, "type") is "function_call" or "custom_tool_call") continue;
            state.Item["phase"] = "commentary";
            await Event("response.output_item.done", ("output_index", state.Index), ("item", WebSearchPayload.DeepCopyObject(state.Item)), ("agent", state.Item["agent"]));
        }
    }
}
