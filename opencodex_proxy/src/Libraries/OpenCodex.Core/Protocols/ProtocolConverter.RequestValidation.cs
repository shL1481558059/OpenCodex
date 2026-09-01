using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static void ValidateRequestSemanticCompatibility(
        Dictionary<string, object?> payload,
        string sourceProtocol,
        string targetProtocol)
    {
        foreach (var parameter in UnsupportedSemanticParameters(sourceProtocol, targetProtocol))
        {
            if (!HasNonNullValue(payload, parameter))
            {
                continue;
            }

            throw new BadRequestException(
                $"request parameter '{parameter}' cannot be converted from {sourceProtocol} to {targetProtocol} without changing request semantics");
        }
    }

    private static IReadOnlyList<string> UnsupportedSemanticParameters(string sourceProtocol, string targetProtocol)
    {
        if (sourceProtocol == Responses && targetProtocol is Chat or Messages)
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

        if (sourceProtocol == Messages && targetProtocol is Responses or Chat)
        {
            return ["container", "thinking"];
        }

        if (sourceProtocol == Chat && targetProtocol == Messages)
        {
            return ["parallel_tool_calls", "reasoning_effort"];
        }

        return [];
    }
}
