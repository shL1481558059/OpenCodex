namespace OpenCodex.Api.Protocols;

public static partial class ProtocolConverter
{
    private static Dictionary<string, object?> ResponsesUsageToCanonical(Dictionary<string, object?> usage)
    {
        return Obj(
            ("input_tokens", ToInt(GetValue(usage, "input_tokens") ?? GetValue(usage, "prompt_tokens"))),
            ("output_tokens", ToInt(GetValue(usage, "output_tokens") ?? GetValue(usage, "completion_tokens"))),
            ("total_tokens", ToInt(GetValue(usage, "total_tokens"))));
    }

    private static Dictionary<string, object?> ChatUsageToCanonical(Dictionary<string, object?> usage)
    {
        return Obj(
            ("input_tokens", ToInt(GetValue(usage, "prompt_tokens") ?? GetValue(usage, "input_tokens"))),
            ("output_tokens", ToInt(GetValue(usage, "completion_tokens") ?? GetValue(usage, "output_tokens"))),
            ("total_tokens", ToInt(GetValue(usage, "total_tokens"))));
    }

    private static Dictionary<string, object?> MessagesUsageToCanonical(Dictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        return Obj(
            ("input_tokens", inputTokens),
            ("output_tokens", outputTokens),
            ("total_tokens", inputTokens + outputTokens));
    }

    private static Dictionary<string, object?> CanonicalUsageToResponses(Dictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var totalTokens = ToInt(GetValue(usage, "total_tokens"));
        return Obj(
            ("input_tokens", inputTokens),
            ("output_tokens", outputTokens),
            ("total_tokens", totalTokens == 0 ? inputTokens + outputTokens : totalTokens));
    }

    private static Dictionary<string, object?> CanonicalUsageToChat(Dictionary<string, object?> usage)
    {
        var inputTokens = ToInt(GetValue(usage, "input_tokens"));
        var outputTokens = ToInt(GetValue(usage, "output_tokens"));
        var totalTokens = ToInt(GetValue(usage, "total_tokens"));
        return Obj(
            ("prompt_tokens", inputTokens),
            ("completion_tokens", outputTokens),
            ("total_tokens", totalTokens == 0 ? inputTokens + outputTokens : totalTokens));
    }

    private static Dictionary<string, object?> CanonicalUsageToMessages(Dictionary<string, object?> usage)
    {
        return Obj(
            ("input_tokens", ToInt(GetValue(usage, "input_tokens"))),
            ("output_tokens", ToInt(GetValue(usage, "output_tokens"))));
    }
}
