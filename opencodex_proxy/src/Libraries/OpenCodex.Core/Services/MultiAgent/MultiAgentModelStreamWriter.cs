using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Streaming;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Collects the existing Responses stream conversion without writing to the client response.</summary>
public sealed class MultiAgentModelStreamWriter(
    Func<Dictionary<string, object?>, CancellationToken, Task>? onEvent = null,
    SseFrameOptions? frameOptions = null) : IProxyStreamWriter
{
    private readonly SseFrameOptions _frameOptions = frameOptions ?? new();
    private readonly SortedDictionary<int, object?> _items = [];
    private readonly Dictionary<int, (long Bytes, long Fields)> _itemBudgets = [];
    private long _itemBytes;
    private long _itemFields;
    private bool _terminalAccepted;

    public Dictionary<string, object?>? Result { get; private set; }

    public void PrepareSse() { }

    public Task<StreamWriteMetrics> WriteLinesAsync(IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default) =>
        WriteFramesAsync(SseFrameReader.ReadChunks(lines, cancellationToken, _frameOptions),
            countsForTtft, elapsedMilliseconds, cancellationToken);

    public Task<StreamWriteMetrics> WriteFrameBatchAsync(IAsyncEnumerable<SseFrame> frames,Func<string,bool> countsForTtft,
        Func<int> elapsedMilliseconds,CancellationToken cancellationToken=default) =>
        WriteCoreAsync(frames,countsForTtft,elapsedMilliseconds,false,cancellationToken);

    public Task<StreamWriteMetrics> WriteFramesAsync(IAsyncEnumerable<SseFrame> frames,Func<string,bool> countsForTtft,
        Func<int> elapsedMilliseconds,CancellationToken cancellationToken=default) =>
        WriteCoreAsync(frames,countsForTtft,elapsedMilliseconds,true,cancellationToken);

    private async Task<StreamWriteMetrics> WriteCoreAsync(IAsyncEnumerable<SseFrame> frames,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds,
        bool finalBatch, CancellationToken cancellationToken = default)
    {
        if (_frameOptions.MaxEventBytes <= 0 || _frameOptions.MaxCaptureBytes <= 0
            || _frameOptions.MaxCaptureItems <= 0 || _frameOptions.MaxCaptureFields <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameOptions));
        var metrics = new StreamWriteMetrics();
        // One writer owns one model response, including all audited batches. A later
        // request gets a new writer; batch cleanup must not reopen this terminal gate.
        if (_terminalAccepted) return metrics;
        var accepted = false;
        try
        {
            await foreach (var frame in frames.WithCancellation(cancellationToken))
            {
                if (_terminalAccepted) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Utf8Length > _frameOptions.MaxEventBytes)
                    throw new UpstreamException("The multi-agent model event exceeds configured MaxEventBytes.");
                if (metrics.TtftMs is null && SseFramePredicate.Evaluate(countsForTtft,frame)) metrics.TtftMs = elapsedMilliseconds();
                if (frame.Event is null || frame.IsDone) continue;
                if (frame.Malformed || frame.Event.Data is not IReadOnlyDictionary<string, object?> source)
                    throw new UpstreamException("The multi-agent model stream contains an invalid JSON event: the payload must be an object.");
                // Callbacks historically own a mutable event. Do not expose the shared frame
                // graph to them, and snapshot Result before an extension can mutate its copy.
                await AcceptPayload(WebSearchPayload.DeepCopyObject(source), cancellationToken);
                // Stop reading at the first terminal. Do not parse, capture or forward
                // trailing model frames, even when they arrive in the same chunk.
                if (_terminalAccepted) break;
            }
            if (finalBatch && Result is null)
                throw new UpstreamException("The multi-agent model stream ended without a terminal Responses event.");
            if (finalBatch && JsonDictionaryValue.String(Result!, "status") == "completed"
                && JsonDictionaryValue.List(Result!, "output").Count == 0)
                throw new UpstreamException("The multi-agent model stream completed without any output items.");
            accepted = true;
            return metrics;
        }
        finally { if (finalBatch || !accepted || _terminalAccepted) ClearOutputItems(); }
    }

    private async Task AcceptPayload(Dictionary<string, object?> payload, CancellationToken ct)
    {
        if (_terminalAccepted) return;
        var type = JsonDictionaryValue.String(payload, "type");
        if (type == "response.output_item.done")
        {
            var index = WebSearchPayload.ToInt(JsonDictionaryValue.Get(payload, "output_index"), _items.Count);
            if (JsonDictionaryValue.Get(payload, "item") is IReadOnlyDictionary<string, object?> item)
                CaptureOutputItem(index, item, ct);
        }
        if (type is "response.completed" or "response.failed" or "response.incomplete")
        {
            if (JsonDictionaryValue.Get(payload, "response") is not IReadOnlyDictionary<string, object?> response)
                throw new UpstreamException("The terminal model event is missing its Responses object.");
            var candidate = WebSearchPayload.DeepCopyObject(response);
            if (JsonDictionaryValue.List(candidate, "output").Count == 0 && _items.Count > 0)
                candidate["output"] = _items.Values.ToList();
            if (JsonDictionaryValue.String(candidate,"status") == "completed" && JsonDictionaryValue.List(candidate,"output").Count == 0)
                throw new UpstreamException("The multi-agent model stream completed without any output items.");
            // The complete history candidate includes metadata and any reconstructed output.
            // Do not replace payload.response: the owner independently preflights the actual
            // terminal wrapper with its wire encoder, then commits history before sending.
            ValidateCaptureBytes(ProtocolJsonWriter.Count(candidate, audit: true, ct));
            ValidateStructure(candidate, ct);
            Result = candidate;
            // Register ownership before the callback can await, re-enter or throw.
            // The first terminal Result remains authoritative after a send failure too.
            _terminalAccepted = true;
        }
        if (type == "error") _terminalAccepted = true;
        if (onEvent is not null) await onEvent(payload, ct);
        if (type == "error") throw new UpstreamException("The multi-agent model stream returned an error event.", body: payload);
    }

    private void CaptureOutputItem(int index, IReadOnlyDictionary<string, object?> item, CancellationToken ct)
    {
        var replacesItem = _itemBudgets.TryGetValue(index, out var previous);
        var itemCount = checked(_items.Count + (replacesItem ? 0 : 1));
        ValidateCollectionItems(itemCount);
        var snapshot = WebSearchPayload.DeepCopyObject(item);
        var bytes = ProtocolJsonWriter.Count(snapshot, audit: true, ct);
        var fields = ValidateStructure(snapshot, ct);
        var candidateBytes = checked(_itemBytes - previous.Bytes + bytes);
        var candidateFields = checked(_itemFields - previous.Fields + fields);
        // Exact JSON array overhead: two brackets and one comma between retained items.
        ValidateCaptureBytes(checked(candidateBytes + 2L + itemCount - 1L));
        ValidateFieldCount(candidateFields);

        // Preserve same-index replacement, but publish only after the replacement fits.
        // An overwritten item does not retain a second byte/field charge.
        _items[index] = snapshot;
        _itemBudgets[index] = (bytes, fields);
        _itemBytes = candidateBytes;
        _itemFields = candidateFields;
    }

    private long ValidateStructure(object? value, CancellationToken ct)
    {
        long fields = 0;
        Visit(value);
        return fields;

        void AddFields(long count)
        {
            fields = checked(fields + count);
            ValidateFieldCount(fields);
        }

        void Visit(object? node)
        {
            ct.ThrowIfCancellationRequested();
            switch (node)
            {
                case IReadOnlyDictionary<string, object?> map:
                    AddFields(map.Count);
                    foreach (var child in map.Values) Visit(child);
                    break;
                case IEnumerable<object?> items:
                    long collectionItems = 0;
                    foreach (var child in items)
                    {
                        ValidateCollectionItems(++collectionItems);
                        Visit(child);
                    }
                    break;
                case JsonElement element:
                    VisitJson(element);
                    break;
                case null or string or bool or byte or sbyte or short or ushort or int or uint
                    or long or ulong or float or double or decimal:
                    break;
                default:
                    // Typed extension dictionaries/arrays/DTOs must obey the same JSON
                    // structure limits, not disappear from the weakly typed graph count.
                    VisitJson(JsonSerializer.SerializeToElement(node));
                    break;
            }
        }

        void VisitJson(JsonElement element)
        {
            ct.ThrowIfCancellationRequested();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    AddFields(1);
                    VisitJson(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                ValidateCollectionItems(element.GetArrayLength());
                foreach (var child in element.EnumerateArray()) VisitJson(child);
            }
        }
    }

    private void ValidateCaptureBytes(long bytes)
    {
        if (bytes > _frameOptions.MaxCaptureBytes) throw CaptureLimit("MaxCaptureBytes");
    }

    private void ValidateCollectionItems(long count)
    {
        if (count > _frameOptions.MaxCaptureItems) throw CaptureLimit("MaxCaptureItems");
    }

    private void ValidateFieldCount(long count)
    {
        if (count > _frameOptions.MaxCaptureFields) throw CaptureLimit("MaxCaptureFields");
    }

    private static UpstreamException CaptureLimit(string limit) => new(
        $"The multi-agent model output exceeds configured {limit}; the complete response was not truncated.",
        errorCode: "output_capture_limit");

    private void ClearOutputItems()
    {
        _items.Clear();
        _itemBudgets.Clear();
        _itemBytes = 0;
        _itemFields = 0;
    }
}
