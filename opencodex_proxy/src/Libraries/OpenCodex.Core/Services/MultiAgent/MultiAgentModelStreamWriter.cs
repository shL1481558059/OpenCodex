using OpenCodex.Core.Protocols;
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
        if (_frameOptions.MaxEventBytes <= 0) throw new ArgumentOutOfRangeException(nameof(frameOptions));
        var metrics = new StreamWriteMetrics();
        var accepted = false;
        try
        {
            await foreach (var frame in frames.WithCancellation(cancellationToken))
            {
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
            }
            if (finalBatch && Result is null)
                throw new UpstreamException("The multi-agent model stream ended without a terminal Responses event.");
            if (finalBatch && JsonDictionaryValue.String(Result!, "status") == "completed"
                && JsonDictionaryValue.List(Result!, "output").Count == 0)
                throw new UpstreamException("The multi-agent model stream completed without any output items.");
            accepted = true;
            return metrics;
        }
        finally { if (finalBatch || !accepted) _items.Clear(); }
    }

    private async Task AcceptPayload(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var type = JsonDictionaryValue.String(payload, "type");
        if (type == "response.output_item.done")
        {
            var index = WebSearchPayload.ToInt(JsonDictionaryValue.Get(payload, "output_index"), _items.Count);
            if (JsonDictionaryValue.Get(payload, "item") is IReadOnlyDictionary<string, object?> item)
                _items[index] = WebSearchPayload.DeepCopyObject(item);
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
            Result = candidate;
        }
        if (onEvent is not null) await onEvent(payload, ct);
        if (type == "error") throw new UpstreamException("The multi-agent model stream returned an error event.", body: payload);
    }
}
