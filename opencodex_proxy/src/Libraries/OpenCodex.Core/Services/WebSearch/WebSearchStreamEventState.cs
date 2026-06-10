using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

internal sealed class WebSearchStreamEventState
{
    private int _sequenceNumber;
    private int _nextOutputIndex;

    private WebSearchStreamEventState(int sequenceNumber, int nextOutputIndex)
    {
        _sequenceNumber = sequenceNumber;
        _nextOutputIndex = nextOutputIndex;
    }

    public int SequenceNumber => _sequenceNumber;

    public int NextOutputIndex => _nextOutputIndex;

    public static WebSearchStreamEventState FromEvents(IReadOnlyList<string> events)
    {
        return new WebSearchStreamEventState(
            NextSequenceNumber(events),
            CalculateNextOutputIndex(events));
    }

    public void ObserveEvents(IReadOnlyList<string> events)
    {
        _sequenceNumber = Math.Max(_sequenceNumber, NextSequenceNumber(events));
        _nextOutputIndex = Math.Max(_nextOutputIndex, CalculateNextOutputIndex(events));
    }

    public string EmitWebSearchAdded(
        string itemId,
        string query,
        out int outputIndex)
    {
        outputIndex = _nextOutputIndex++;
        return Emit(
            "response.output_item.added",
            new Dictionary<string, object?>
            {
                ["output_index"] = outputIndex,
                ["item"] = new Dictionary<string, object?>
                {
                    ["id"] = itemId,
                    ["type"] = "web_search_call",
                    ["status"] = "in_progress",
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "search",
                        ["query"] = query
                    }
                }
            });
    }

    public string EmitWebSearchDone(int outputIndex, WebSearchToolResult result)
    {
        return Emit(
            "response.output_item.done",
            new Dictionary<string, object?>
            {
                ["output_index"] = outputIndex,
                ["item"] = WebSearchResponsePayload.BuildWebSearchItem(result, includeResult: true)
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

    private static int NextSequenceNumber(IReadOnlyList<string> events)
    {
        var next = 0;
        foreach (var line in events)
        {
            var (_, payload) = WebSearchResponsePayload.ParseSseLine(line);
            var value = ToInt(GetValue(payload ?? [], "sequence_number"), -1);
            if (value >= next)
            {
                next = value + 1;
            }
        }

        return next;
    }

    private static int CalculateNextOutputIndex(IReadOnlyList<string> events)
    {
        var next = 0;
        foreach (var line in events)
        {
            var (_, payload) = WebSearchResponsePayload.ParseSseLine(line);
            next = Math.Max(next, MaxOutputIndex(payload) + 1);
        }

        return next;
    }

    private static int MaxOutputIndex(object? value)
    {
        if (TryAsObject(value, out var dictionary))
        {
            var max = -1;
            foreach (var (key, item) in dictionary)
            {
                if (key == "output_index")
                {
                    max = Math.Max(max, ToInt(item, -1));
                }
                else
                {
                    max = Math.Max(max, MaxOutputIndex(item));
                }
            }

            return max;
        }

        if (TryAsList(value, out var list))
        {
            return list.Select(MaxOutputIndex).DefaultIfEmpty(-1).Max();
        }

        return -1;
    }
}
