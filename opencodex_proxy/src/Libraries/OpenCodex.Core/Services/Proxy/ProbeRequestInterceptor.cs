using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public static class ProbeRequestInterceptor
{
    public static bool TryIntercept(
        string entryProtocol,
        Dictionary<string, object?> payload,
        string requestId,
        out ProxyEndpointResult? result)
    {
        result = null;
        if (!TryGetProbeMaxTokens(payload, out _))
        {
            return false;
        }

        var model = JsonDictionaryValue.String(payload, "model");
        result = new ProxyEndpointResult(200, BuildProbeResponse(entryProtocol, model, requestId), IsEmpty: false);
        return true;
    }

    private static bool TryGetProbeMaxTokens(Dictionary<string, object?> payload, out int maxTokens)
    {
        maxTokens = 0;
        foreach (var key in new[] { "max_tokens", "max_output_tokens", "max_completion_tokens" })
        {
            if (payload.TryGetValue(key, out var value) && value is int intValue && intValue <= 1)
            {
                maxTokens = intValue;
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, object?> BuildProbeResponse(
        string entryProtocol,
        string? model,
        string requestId)
    {
        var shortId = requestId.Replace("-", string.Empty);
        if (shortId.Length > 24)
        {
            shortId = shortId[..24];
        }

        if (entryProtocol == ProtocolConverter.Messages)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = $"msg_{shortId}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model ?? "claude-opus-5",
                ["content"] = Array.Empty<object>(),
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = null
            };
        }

        if (entryProtocol == ProtocolConverter.Chat)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = $"chatcmpl-{shortId}",
                ["object"] = "chat.completion",
                ["created"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model ?? "gpt-5.5",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty
                        },
                        ["finish_reason"] = "stop"
                    }
                }
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = $"resp_{shortId}",
            ["object"] = "response",
            ["status"] = "completed",
            ["model"] = model ?? "gpt-5.5",
            ["output"] = Array.Empty<object>()
        };
    }
}
