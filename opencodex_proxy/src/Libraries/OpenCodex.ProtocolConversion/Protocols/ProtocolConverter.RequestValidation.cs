using OpenCodex.ProtocolConversion.Errors;

namespace OpenCodex.ProtocolConversion;

public static partial class ProtocolConverter
{
    internal static void ValidateRequestSemanticCompatibility(
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

    internal static void ValidateCanonicalRequestSemanticCompatibility(
        Dictionary<string, object?> parameters,
        string? sourceProtocol,
        string targetProtocol)
    {
        if (sourceProtocol is not null)
        {
            ValidateRequestSemanticCompatibility(parameters, sourceProtocol, targetProtocol);
            return;
        }

        // An explicitly constructed canonical graph is protocol-neutral, not an inferred wire source.
        var protocols = new[] { Responses, Chat, Messages };
        var sourceSpecific = protocols.SelectMany(source => protocols.SelectMany(target =>
            UnsupportedSemanticParameters(source, target))).Distinct(StringComparer.Ordinal);
        foreach (var parameter in sourceSpecific)
        {
            if (HasNonNullValue(parameters, parameter))
                throw new BadRequestException($"CanonicalRequest.SourceProtocol is required for source-specific parameter '{parameter}'.");
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
            return targetProtocol == Chat
                ? ["container"]
                : ["container", "thinking"];
        }

        if (sourceProtocol == Chat && targetProtocol == Messages)
        {
            return ["parallel_tool_calls", "reasoning_effort"];
        }

        return [];
    }
}
