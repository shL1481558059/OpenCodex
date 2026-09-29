using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Collects the existing Responses stream conversion without writing to the client response.</summary>
public sealed class MultiAgentModelStreamWriter(
    Func<Dictionary<string, object?>, CancellationToken, Task>? onEvent = null) : IProxyStreamWriter
{
    private readonly StringBuilder _line = new();
    private readonly List<string> _data = [];
    private readonly SortedDictionary<int, object?> _items = [];

    public Dictionary<string, object?>? Result { get; private set; }

    public void PrepareSse() { }

    public async Task<StreamWriteMetrics> WriteLinesAsync(IAsyncEnumerable<string> lines,
        Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var metrics = new StreamWriteMetrics();
        await foreach (var chunk in lines.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metrics.TtftMs is null && countsForTtft(chunk)) metrics.TtftMs = elapsedMilliseconds();
            foreach (var character in chunk)
            {
                if (character == '\n')
                {
                    await AcceptLine(_line.ToString().TrimEnd('\r'), cancellationToken);
                    _line.Clear();
                }
                else _line.Append(character);
            }
        }
        if (_line.Length > 0) await AcceptLine(_line.ToString().TrimEnd('\r'), cancellationToken);
        await CompleteEvent(cancellationToken);
        if (Result is null)
            throw new UpstreamException("The multi-agent model stream ended without a terminal Responses event.");
        if (JsonDictionaryValue.String(Result, "status") == "completed"
            && JsonDictionaryValue.List(Result, "output").Count == 0)
            throw new UpstreamException("The multi-agent model stream completed without any output items.");
        return metrics;
    }

    private async Task AcceptLine(string line, CancellationToken ct)
    {
        if (line.Length == 0) await CompleteEvent(ct);
        else if (line.StartsWith("data:", StringComparison.Ordinal)) _data.Add(line[5..].TrimStart());
    }

    private async Task CompleteEvent(CancellationToken ct)
    {
        if (_data.Count == 0) return;
        var json = string.Join("\n", _data);
        _data.Clear();
        if (json == "[DONE]") return;
        Dictionary<string, object?> payload;
        try
        {
            using var document = JsonDocument.Parse(json);
            payload = WebSearchPayload.FromJsonElement(document.RootElement) as Dictionary<string, object?>
                ?? throw new JsonException("The event payload must be an object.");
        }
        catch (JsonException exception)
        {
            throw new UpstreamException("The multi-agent model stream contains an invalid JSON event: " + exception.Message);
        }

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
            Result = WebSearchPayload.DeepCopyObject(response);
            if (JsonDictionaryValue.List(Result, "output").Count == 0 && _items.Count > 0)
                Result["output"] = _items.Values.ToList();
        }
        if (onEvent is not null) await onEvent(payload, ct);
        if (type == "error") throw new UpstreamException("The multi-agent model stream returned an error event.", body: payload);
    }
}
