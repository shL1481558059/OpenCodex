using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static void ValidateRequestSemanticCompatibility(
        Dictionary<string, object?> payload,
        string clientProtocol,
        string upstreamProtocol)
    {
        foreach (var parameter in UnsupportedSemanticParameters(clientProtocol, upstreamProtocol))
        {
            if (!HasNonNullValue(payload, parameter))
            {
                continue;
            }

            throw new BadRequestException(
                $"request parameter '{parameter}' cannot be converted from {clientProtocol} to {upstreamProtocol} without changing request semantics");
        }
    }

    private static IReadOnlyList<string> UnsupportedSemanticParameters(string clientProtocol, string upstreamProtocol)
    {
        if (clientProtocol == Responses && upstreamProtocol is Chat or Messages)
        {
            var parameters = new List<string>
            {
                "background",
                "context_management",
                "conversation",
                "previous_response_id",
                "prompt"
            };

            return parameters;
        }

        if (clientProtocol == Messages && upstreamProtocol is Responses or Chat)
        {
            return upstreamProtocol == Chat
                ? ["container"]
                : ["container", "thinking"];
        }

        if (clientProtocol == Chat && upstreamProtocol == Messages)
        {
            return ["parallel_tool_calls", "reasoning_effort"];
        }

        return [];
    }
}
