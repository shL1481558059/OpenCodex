using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal sealed class WebSearchStreamEventState
{
    private int _sequenceNumber;
    private int _nextOutputIndex;
    private string? _responseId;
    private object? _createdAt;
    private readonly SortedDictionary<int, Dictionary<string, object?>> _output = [];
    private readonly Dictionary<string, int> _searchIndices = new(StringComparer.Ordinal);

    public int SequenceNumber => _sequenceNumber;

    public int NextOutputIndex => _nextOutputIndex;

    public bool HasResponse => _responseId is not null;

    public static bool IsTerminal(string line)
    {
        var (eventName, payload) = WebSearchResponsePayload.ParseSseLine(line);
        return StringValue(payload ?? [], "type", eventName)
            is "response.completed" or "response.failed" or "response.incomplete";
    }

    public string Observe(string line, IReadOnlyList<WebSearchToolResult> results)
    {
        var (eventName, payload) = WebSearchResponsePayload.ParseSseLine(line);
        if (payload is null)
        {
            return line;
        }
        if (TryAsObject(GetValue(payload, "response"), out var response))
        {
            _responseId ??= StringValue(response, "id");
            _createdAt ??= GetValue(response, "created_at");
            response["id"] = _responseId;
        }
        if (payload.ContainsKey("response_id"))
        {
            payload["response_id"] = _responseId;
        }
        var index = ToInt(GetValue(payload, "output_index"), -1);
        if (index >= 0)
        {
            _nextOutputIndex = Math.Max(_nextOutputIndex, index + 1);
            if (TryAsObject(GetValue(payload, "item"), out var item))
            {
                foreach (var block in ListValue(item, "content").OfType<Dictionary<string, object?>>())
                {
                    WebSearchResponsePayload.AnnotateTextBlock(block, results);
                }
                _output[index] = DeepCopyObject(item);
            }
        }
        if (TryAsObject(GetValue(payload, "part"), out var part))
        {
            WebSearchResponsePayload.AnnotateTextBlock(part, results);
        }
        return Emit(eventName, payload);
    }

    public string Complete(
        string line,
        Dictionary<string, object?>? aggregateUsage,
        Dictionary<string, object?>? overrideResponse,
        out Dictionary<string, object?> finalResponse)
    {
        var (eventName, payload) = WebSearchResponsePayload.ParseSseLine(line);
        payload ??= new Dictionary<string, object?>();
        var response = ObjectValue(payload, "response");
        response["id"] = _responseId ?? StringValue(response, "id");
        if (_createdAt is not null)
        {
            response["created_at"] = _createdAt;
        }
        response["output"] = _output.Values.Cast<object?>().ToList();
        if (aggregateUsage is not null)
        {
            response["usage"] = DeepCopyObject(aggregateUsage);
        }
        if (eventName != "response.failed"
            && (StringValue(response, "status") == "incomplete" || StringValue(overrideResponse ?? [], "status") == "incomplete"))
        {
            eventName = "response.incomplete";
            response["status"] = "incomplete";
            response["incomplete_details"] = GetValue(overrideResponse ?? [], "incomplete_details")
                ?? GetValue(response, "incomplete_details");
        }
        payload["response"] = response;
        finalResponse = DeepCopyObject(response);
        return Emit(eventName, payload);
    }

    public int SearchIndex(string itemId) => _searchIndices[itemId];

    public string EmitWebSearchInProgress(string itemId) => EmitWebSearchStatus(itemId, "in_progress");

    public string EmitWebSearchSearching(string itemId) => EmitWebSearchStatus(itemId, "searching");

    public string EmitWebSearchCompleted(string itemId) => EmitWebSearchStatus(itemId, "completed");

    private string EmitWebSearchStatus(string itemId, string status)
    {
        var index = SearchIndex(itemId);
        _output[index]["status"] = status;
        return Emit($"response.web_search_call.{status}", new Dictionary<string, object?>
        {
            ["item_id"] = itemId,
            ["output_index"] = index
        });
    }

    public string EmitWebSearchAdded(
        string itemId,
        string query,
        out int outputIndex)
    {
        outputIndex = _nextOutputIndex++;
        _searchIndices[itemId] = outputIndex;
        var item = new Dictionary<string, object?>
        {
            ["id"] = itemId,
            ["type"] = "web_search_call",
            ["status"] = "in_progress",
            ["action"] = new Dictionary<string, object?> { ["type"] = "search", ["query"] = query }
        };
        _output[outputIndex] = item;
        return Emit(
            "response.output_item.added",
            new Dictionary<string, object?>
            {
                ["output_index"] = outputIndex,
                ["item"] = item
            });
    }

    public string EmitWebSearchDone(int outputIndex, WebSearchToolResult result, bool includeSources = false)
    {
        var item = WebSearchResponsePayload.BuildWebSearchItem(result, true, includeSources);
        _output[outputIndex] = item;
        return Emit(
            "response.output_item.done",
            new Dictionary<string, object?>
            {
                ["output_index"] = outputIndex,
                ["item"] = item
            });
    }

    private string Emit(
        string eventName,
        Dictionary<string, object?> payload)
    {
        var enriched = new Dictionary<string, object?>(payload, StringComparer.Ordinal)
        {
            ["type"] = eventName,
            ["sequence_number"] = _sequenceNumber++
        };
        return $"event: {eventName}\ndata: {JsonDumps(enriched)}\n\n";
    }

}
