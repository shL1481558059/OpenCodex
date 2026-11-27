namespace OpenCodex.ProtocolConversion;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ResponsesUsageToCanonical(Dictionary<string, object?> usage)
    {
        var result = DeepCopy(usage) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        result["input_tokens"] = ToInt(GetValue(usage, "input_tokens") ?? GetValue(usage, "prompt_tokens"));
        result["output_tokens"] = ToInt(GetValue(usage, "output_tokens") ?? GetValue(usage, "completion_tokens"));
        result["total_tokens"] = ToInt(GetValue(usage, "total_tokens"));
        result["cached_tokens"] = NestedCachedTokens(usage, "input_tokens_details");
        return result;
    }

    private static Dictionary<string, object?> ChatUsageToCanonical(Dictionary<string, object?> usage)
    {
        var result = DeepCopy(usage) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        result["input_tokens"] = ToInt(GetValue(usage, "prompt_tokens") ?? GetValue(usage, "input_tokens"));
        result["output_tokens"] = ToInt(GetValue(usage, "completion_tokens") ?? GetValue(usage, "output_tokens"));
        result["total_tokens"] = ToInt(GetValue(usage, "total_tokens"));
        result["cached_tokens"] = NestedCachedTokens(usage, "prompt_tokens_details", "input_tokens_details");
        return result;
    }

    private static Dictionary<string, object?> MessagesUsageToCanonical(Dictionary<string, object?> usage)
    {
        var result = DeepCopy(usage) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var cachedTokens = ToInt(GetValue(usage, "cache_creation_input_tokens"))
            + ToInt(GetValue(usage, "cache_read_input_tokens"));
        result["input_tokens"] = inputTokens;
        result["output_tokens"] = outputTokens;
        result["total_tokens"] = inputTokens + outputTokens;
        result["cached_tokens"] = cachedTokens;
        return result;
    }

    private static Dictionary<string, object?> CanonicalUsageToResponses(Dictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var totalTokens = ToInt(GetValue(usage, "total_tokens"));
        var result = Obj(
            ("input_tokens", inputTokens),
            ("output_tokens", outputTokens),
            ("total_tokens", totalTokens == 0 ? inputTokens + outputTokens : totalTokens));

        var cachedTokens = ToInt(GetValue(usage, "cached_tokens"));
        if (cachedTokens > 0)
        {
            result["input_tokens_details"] = Obj(("cached_tokens", cachedTokens));
        }

        return result;
    }

    private static Dictionary<string, object?> CanonicalUsageToChat(Dictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var totalTokens = ToInt(GetValue(usage, "total_tokens"));
        var result = Obj(
            ("prompt_tokens", inputTokens),
            ("completion_tokens", outputTokens),
            ("total_tokens", totalTokens == 0 ? inputTokens + outputTokens : totalTokens));

        var cachedTokens = ToInt(GetValue(usage, "cached_tokens"));
        if (cachedTokens > 0)
        {
            result["prompt_tokens_details"] = Obj(("cached_tokens", cachedTokens));
        }

        return result;
    }

    private static Dictionary<string, object?> CanonicalUsageToMessages(Dictionary<string, object?> usage)
    {
        var result = Obj(
            ("input_tokens", ToInt(GetValue(usage, "input_tokens"))),
            ("output_tokens", ToInt(GetValue(usage, "output_tokens"))));

        var cachedTokens = ToInt(GetValue(usage, "cached_tokens"));
        if (cachedTokens > 0)
        {
            result["cache_read_input_tokens"] = cachedTokens;
        }

        return result;
    }

    private static int NestedCachedTokens(Dictionary<string, object?> usage, params string[] detailKeys)
    {
        foreach (var key in detailKeys)
        {
            if (TryAsObject(GetValue(usage, key), out var details))
            {
                return ToInt(GetValue(details, "cached_tokens"));
            }
        }

        return 0;
    }
}
