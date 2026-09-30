using System.Text.Json;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    internal static bool UsesCustomInputEnvelope(
        object? name,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        if (!TryGetResponsesToolCallMapping(toolName, mappings, out var mapping)
            || mapping.NativeType is not ("custom" or "custom_tool"))
            return false;

        // apply_patch has its own argument contract and decoder.
        return ResolveResponsesToolCallShape(toolName, mappings).Kind == ResponsesToolCallKind.NativeTool;
    }

    internal static string DecodeCustomToolArguments(object? arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonDumps(arguments));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var properties = document.RootElement.EnumerateObject();
                if (properties.MoveNext())
                {
                    var input = properties.Current;
                    if (input.NameEquals("input") && input.Value.ValueKind == JsonValueKind.String
                        && !properties.MoveNext())
                        return input.Value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            // Report the contract violation without including tool arguments.
        }

        throw new UpstreamException("Custom tool arguments must be a JSON object containing exactly one string input property.");
    }
}
