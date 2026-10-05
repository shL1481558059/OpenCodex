using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using System.Text.Json;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Executes one client-owned Responses request and publishes committed tool batches.</summary>
public sealed class NativeClientResponseExecutor(NativeClientSessionStore store)
{
    public async Task<D> ExecuteAsync(NativeClientSession session, D request, string turnId,
        MultiAgentModelCall model, Func<D, Task> emit, CancellationToken ct)
    {
        await using var lease = await store.AcquireRequestAsync(session, ct);
        var projection = await store.ProjectRequestAsync(session, request, turnId, lease.Token);
        var sequence = 0;
        var responseId = "";
        var created = false;
        var inProgress = false;
        var buffering = false;
        var terminalSent = false;
        var buffered = new List<D>();
        var validationEvents = new List<D>();
        var added = new HashSet<int>();
        var done = new HashSet<int>();
        var itemIds = new Dictionary<int, string>();

        async Task Send(D value)
        {
            lease.Token.ThrowIfCancellationRequested();
            var copy = WebSearchPayload.DeepCopyObject(value);
            copy["sequence_number"] = ++sequence;
            await emit(copy);
        }

        void CaptureId(D response)
        {
            var id = Text(response, "id");
            if (id.Length == 0) return;
            if (responseId.Length > 0 && responseId != id)
                throw new UpstreamException("The model stream changed its response id.");
            responseId = id;
        }

        void SetItemId(D item, int index)
        {
            var id = Text(item, "id");
            if (id.Length == 0) id = itemIds.GetValueOrDefault(index) ?? "item_native_" + Guid.NewGuid().ToString("N");
            if (itemIds.TryGetValue(index, out var previous) && previous != id)
                throw new UpstreamException("The model stream changed an output item id.");
            itemIds[index] = id;
            item["id"] = id;
        }

        async Task EnsureStarted(D response)
        {
            var initial = WebSearchPayload.DeepCopyObject(response);
            initial["status"] = "in_progress";
            initial["output"] = new List<object?>();
            initial.Remove("error");
            initial.Remove("incomplete_details");
            if (!created) { await Send(new() { ["type"] = "response.created", ["response"] = initial }); created = true; }
            if (!inProgress) { await Send(new() { ["type"] = "response.in_progress", ["response"] = initial }); inProgress = true; }
        }

        async Task OnEvent(D source, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var value = WebSearchPayload.DeepCopyObject(source);
            var type = Text(value, "type");
            if (JsonDictionaryValue.Get(value, "response") is D response) CaptureId(response);
            if (type is "response.completed" or "response.failed" or "response.incomplete" or "error") return;
            if (type == "response.created")
            {
                if (created) return;
                created = true;
            }
            if (type == "response.in_progress")
            {
                if (inProgress) return;
                inProgress = true;
            }
            var index = WebSearchPayload.ToInt(JsonDictionaryValue.Get(value, "output_index"), -1);
            if (index >= 0 && JsonDictionaryValue.Get(value, "item") is D item) SetItemId(item, index);
            var argumentEvent = type.StartsWith("response.function_call_arguments.", StringComparison.Ordinal)
                || type.StartsWith("response.custom_tool_call_input.", StringComparison.Ordinal);
            var itemEvent = type is "response.output_item.added" or "response.output_item.done";
            if (itemEvent && (index < 0 || JsonDictionaryValue.Get(value, "item") is not D))
                throw new UpstreamException("The model stream output item is missing its index or payload.");
            var executable = JsonDictionaryValue.Get(value, "item") is D output && IsToolShape(output) || argumentEvent;
            buffering |= executable || type.EndsWith(".done", StringComparison.Ordinal);
            if (type == "response.output_item.added" && !added.Add(index))
                throw new UpstreamException("The model stream repeated an output item added event.");
            if (type == "response.output_item.done" && !done.Add(index))
                throw new UpstreamException("The model stream repeated an output item done event.");
            if (type == "response.output_item.done" && !added.Contains(index))
                throw new UpstreamException("The model stream completed an output item before adding it.");
            if (itemEvent || argumentEvent) validationEvents.Add(value);
            if (buffering) buffered.Add(value);
            else await Send(value);
        }

        async Task Publish(D response, bool replay)
        {
            await EnsureStarted(response);
            if (!replay)
                foreach (var value in buffered) await Send(value);
            var output = JsonDictionaryValue.List(response, "output").OfType<D>().ToList();
            for (var index = 0; index < output.Count; index++)
            {
                if (replay || !added.Contains(index))
                    foreach (var value in ItemEvents(output[index], index)) await Send(value);
                else if (!done.Contains(index))
                    await Send(new() { ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = output[index] });
            }
            terminalSent = true;
            await Send(new() { ["type"] = "response.completed", ["response"] = response });
        }

        try
        {
            if (projection.ReplayResponse is { } replay)
            {
                CaptureId(replay);
                await Publish(replay, true);
                return replay;
            }
            var result = WebSearchPayload.DeepCopyObject(await model(projection.Payload, OnEvent, lease.Token));
            lease.Token.ThrowIfCancellationRequested();
            CaptureId(result);
            if (responseId.Length == 0) responseId = "resp_native_" + Guid.NewGuid().ToString("N");
            result["id"] = responseId;
            result.TryAdd("object", "response");
            result.TryAdd("model", JsonDictionaryValue.String(projection.Payload, "model"));
            var status = Text(result, "status");
            if (status == "failed")
            {
                await EnsureStarted(result);
                terminalSent = true;
                await Send(new() { ["type"] = "response.failed", ["response"] = result });
                return result;
            }
            if (status == "incomplete")
            {
                if (JsonDictionaryValue.Get(result, "incomplete_details") is not D details || Text(details, "reason").Length == 0)
                    throw new UpstreamException("The incomplete response is missing its reason.");
                // Save only as a continuation; retrying this request must call the model again.
                // Incomplete tools never become executable output_item.done events.
                lease.Token.ThrowIfCancellationRequested();
                await store.CommitResponseAsync(session, projection, result, CancellationToken.None);
                await EnsureStarted(result);
                terminalSent = true;
                await Send(new() { ["type"] = "response.incomplete", ["response"] = result });
                return result;
            }
            if (status != "completed") throw new UpstreamException("The model response has no supported terminal status.");
            ProtocolConverter.ValidateResponsesToolCalls(projection.Payload, result);
            var items = JsonDictionaryValue.List(result, "output").OfType<D>().ToList();
            if (!items.Any(i => IsTool(i) || Text(i, "type") == "message" && JsonDictionaryValue.List(i, "content").OfType<D>()
                .Any(p => Text(p, "type") == "output_text" && !string.IsNullOrWhiteSpace(Text(p, "text"))
                    || Text(p, "type") == "refusal" && !string.IsNullOrWhiteSpace(Text(p, "refusal")))))
                throw new UpstreamException("The completed model response contains no nonempty message or declared tool call.");
            for (var index = 0; index < items.Count; index++) SetItemId(items[index], index);
            ValidateStreamTools(items, validationEvents);
            lease.Token.ThrowIfCancellationRequested();
            // Once committing starts, a disconnect cannot leave a partially published tool batch.
            await store.CommitResponseAsync(session, projection, result, CancellationToken.None);
            await Publish(result, false);
            return result;
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested) { throw; }
        catch (Exception error) when (!terminalSent)
        {
            if (responseId.Length == 0) responseId = "resp_native_" + Guid.NewGuid().ToString("N");
            var failed = new D
            {
                ["id"] = responseId, ["object"] = "response", ["status"] = "failed",
                ["model"] = JsonDictionaryValue.String(projection.Payload, "model"), ["output"] = new List<object?>(),
                ["error"] = new D { ["code"] = error is ProxyException proxy ? proxy.ErrorType : "server_error", ["message"] = error.Message }
            };
            if (error is UpstreamException upstream && upstream.Body is D body && JsonDictionaryValue.Get(body, "error") is D upstreamError)
                failed["error"] = WebSearchPayload.DeepCopyObject(upstreamError);
            await EnsureStarted(failed);
            terminalSent = true;
            await Send(new() { ["type"] = "response.failed", ["response"] = failed });
            return failed;
        }
    }

    private static void ValidateStreamTools(IReadOnlyList<D> output, IReadOnlyList<D> events)
    {
        var deltas = new Dictionary<int, string>();
        foreach (var value in events)
        {
            var type = Text(value, "type");
            var item = JsonDictionaryValue.Get(value, "item") as D;
            var argumentEvent = type.StartsWith("response.function_call_arguments.", StringComparison.Ordinal)
                || type.StartsWith("response.custom_tool_call_input.", StringComparison.Ordinal);
            if (item is null && !argumentEvent) continue;
            var index = WebSearchPayload.ToInt(JsonDictionaryValue.Get(value, "output_index"), -1);
            if (index < 0 || index >= output.Count)
                throw new UpstreamException("The streamed tool has no matching completed output item.");
            var expected = output[index];
            // Check both sides: a streamed message or server-search item cannot hide
            // a different executable tool in the terminal response.
            if (item is not null && !IsToolShape(item) && !IsToolShape(expected)) continue;
            if (!IsToolShape(expected))
                throw new UpstreamException("The streamed tool has no matching completed output item.");
            var field = ArgumentField(expected);
            if (item is not null)
            {
                foreach (var identity in new[] { "type", "name", "namespace", "call_id", "id", "execution" })
                    if (Text(item, identity) != Text(expected, identity))
                        throw new UpstreamException("The streamed tool identity differs from its completed output item.");
                var payload = JsonDictionaryValue.Get(item, field);
                var hasPayload = payload is string text ? text.Length > 0
                    : payload is IReadOnlyDictionary<string, object?> structured && structured.Count > 0;
                if ((type == "response.output_item.done" || hasPayload) && !ToolPayloadMatches(expected, payload))
                    throw new UpstreamException("The streamed tool payload differs from its completed output item.");
            }
            else
            {
                if (Text(value, "item_id") != Text(expected, "id"))
                    throw new UpstreamException("The streamed tool argument event targets a different output item.");
                if (!type.StartsWith(ArgumentEventPrefix(expected), StringComparison.Ordinal))
                    throw new UpstreamException("The streamed tool argument event has the wrong tool type.");
                if (type.EndsWith(".delta", StringComparison.Ordinal)) deltas[index] = deltas.GetValueOrDefault(index, "") + Text(value, "delta");
                else if (type.EndsWith(".done", StringComparison.Ordinal) && !ToolPayloadMatches(expected, JsonDictionaryValue.Get(value, field)))
                    throw new UpstreamException("The streamed tool arguments differ from its completed output item.");
            }
        }
        foreach (var (index, value) in deltas)
        {
            if (!ToolPayloadMatches(output[index], value))
                throw new UpstreamException("The streamed tool argument deltas differ from its completed output item.");
        }
    }

    private static IEnumerable<D> ItemEvents(D item, int index)
    {
        var initial = WebSearchPayload.DeepCopyObject(item);
        var type = Text(item, "type");
        if (type is "message" or "reasoning") initial[type == "message" ? "content" : "summary"] = new List<object?>();
        if (type is "function_call" or "tool_search_call") initial["arguments"] = "";
        if (type == "custom_tool_call") initial["input"] = "";
        if (initial.ContainsKey("status")) initial["status"] = "in_progress";
        yield return new() { ["type"] = "response.output_item.added", ["output_index"] = index, ["item"] = initial };
        var itemId = Text(item, "id");
        if (type == "message")
        {
            var content = JsonDictionaryValue.List(item, "content").OfType<D>().ToList();
            for (var partIndex = 0; partIndex < content.Count; partIndex++)
            {
                var part = content[partIndex];
                var field = Text(part, "type") == "refusal" ? "refusal" : "text";
                var eventType = field == "refusal" ? "refusal" : "output_text";
                var initialPart = WebSearchPayload.DeepCopyObject(part); initialPart[field] = "";
                D Event(string name) => new() { ["type"] = name, ["item_id"] = itemId, ["output_index"] = index, ["content_index"] = partIndex };
                var added = Event("response.content_part.added"); added["part"] = initialPart; yield return added;
                var delta = Event("response." + eventType + ".delta"); delta["delta"] = Text(part, field); yield return delta;
                var done = Event("response." + eventType + ".done"); done[field] = Text(part, field); yield return done;
                var completed = Event("response.content_part.done"); completed["part"] = part; yield return completed;
            }
        }
        else if (IsTool(item))
        {
            var field = ArgumentField(item);
            var payload = type == "tool_search_call" ? WebSearchPayload.JsonDumps(JsonDictionaryValue.Get(item, field)) : Text(item, field);
            yield return new() { ["type"] = ArgumentEventPrefix(item) + "delta", ["item_id"] = itemId, ["output_index"] = index, ["delta"] = payload };
            yield return new() { ["type"] = ArgumentEventPrefix(item) + "done", ["item_id"] = itemId, ["output_index"] = index, [field] = payload };
        }
        yield return new() { ["type"] = "response.output_item.done", ["output_index"] = index, ["item"] = item };
    }

    private static bool IsTool(D item) => Text(item, "type") is "function_call" or "custom_tool_call"
        || Text(item, "type") == "tool_search_call" && Text(item, "execution") == "client";
    private static bool IsToolShape(D item) => Text(item, "type") is "function_call" or "custom_tool_call" or "tool_search_call";
    private static string ArgumentField(D item) => Text(item, "type") == "custom_tool_call" ? "input" : "arguments";
    private static string ArgumentEventPrefix(D item) => Text(item, "type") == "custom_tool_call"
        ? "response.custom_tool_call_input." : "response.function_call_arguments.";
    private static bool ToolPayloadMatches(D expected, object? actual)
    {
        if (Text(expected, "type") != "tool_search_call")
            return actual is string text && text == Text(expected, ArgumentField(expected));
        try
        {
            using var supplied = JsonDocument.Parse(actual is string json ? json : JsonSerializer.Serialize(actual));
            using var complete = JsonDocument.Parse(JsonSerializer.Serialize(JsonDictionaryValue.Get(expected, "arguments")));
            return supplied.RootElement.ValueKind == JsonValueKind.Object
                && JsonElement.DeepEquals(supplied.RootElement, complete.RootElement);
        }
        catch (JsonException) { return false; }
    }
    // Wire strings are lossless: trimming a delta changes code, JSON string values,
    // and replayed messages even when the original and final payloads match exactly.
    private static string Text(D value, string key) => JsonDictionaryValue.Get(value, key) as string ?? string.Empty;
}
