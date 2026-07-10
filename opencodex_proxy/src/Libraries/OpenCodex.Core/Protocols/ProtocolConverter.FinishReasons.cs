namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static string ResponsesStatusToCanonicalFinishReason(
        Dictionary<string, object?> payload,
        bool hasToolCalls)
    {
        var status = GetString(payload, "status") ?? "completed";
        if (status == "incomplete")
        {
            var reason = GetString(ObjectValue(payload, "incomplete_details"), "reason") ?? string.Empty;
            return reason == "content_filter" ? "content_filter" : "length";
        }

        if (status is "failed" or "cancelled")
        {
            return "content_filter";
        }

        return hasToolCalls ? "tool_calls" : "stop";
    }

    private static string ChatFinishReasonToCanonical(object? value)
    {
        return Convert.ToString(value) switch
        {
            "length" => "length",
            "tool_calls" or "function_call" => "tool_calls",
            "content_filter" => "content_filter",
            _ => "stop"
        };
    }

    private static string MessagesStopReasonToCanonical(object? value)
    {
        return Convert.ToString(value) switch
        {
            "max_tokens" => "length",
            "tool_use" => "tool_calls",
            "refusal" => "content_filter",
            _ => "stop"
        };
    }

    private static string CanonicalFinishReasonToMessages(string value)
    {
        return value switch
        {
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            "content_filter" => "refusal",
            _ => "end_turn"
        };
    }
}
