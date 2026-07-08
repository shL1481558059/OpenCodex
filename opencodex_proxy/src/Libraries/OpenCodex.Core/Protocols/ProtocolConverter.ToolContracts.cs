namespace OpenCodex.Core.Protocols;

internal enum ResponsesToolCallKind
{
    Function,
    CustomTool,
    NativeTool
}

public sealed class ResponsesToolCallMapping
{
    public string ChatName { get; init; } = string.Empty;
    public string NativeType { get; init; } = "function";
    public string ResponsesName { get; init; } = string.Empty;
    public string? Namespace { get; init; }
}

internal sealed class ResponsesToolCallShape
{
    public ResponsesToolCallKind Kind { get; init; } = ResponsesToolCallKind.Function;
    public string ItemType { get; init; } = "function_call";
    public string ArgumentField { get; init; } = "arguments";
    public string Name { get; init; } = string.Empty;
    public string? Namespace { get; init; }
}

public static partial class ProtocolConverter
{
    internal static ResponsesToolCallKind GetResponsesToolCallKind(
        object? name,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings = null)
    {
        return ResolveResponsesToolCallShape(name, mappings).Kind;
    }

    internal static ResponsesToolCallShape ResolveResponsesToolCallShape(
        object? name,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        if (TryGetResponsesToolCallMapping(toolName, mappings, out var mapping))
        {
            var nativeType = mapping.NativeType.Replace("-", "_", StringComparison.Ordinal);
            if (nativeType == "function")
            {
                return FunctionToolShape(mapping.ResponsesName, mapping.Namespace);
            }

            if (IsApplyPatchName(nativeType) || IsApplyPatchName(mapping.ResponsesName))
            {
                return CustomToolShape(mapping.ResponsesName, mapping.Namespace);
            }

            return new ResponsesToolCallShape
            {
                Kind = ResponsesToolCallKind.NativeTool,
                ItemType = NativeToolCallItemType(nativeType),
                ArgumentField = NativeToolArgumentField(nativeType),
                Name = mapping.ResponsesName,
                Namespace = mapping.Namespace
            };
        }

        var normalized = toolName.Replace("-", "_", StringComparison.Ordinal);
        return IsApplyPatchName(normalized)
            ? CustomToolShape(toolName, null)
            : FunctionToolShape(toolName, null);
    }

    private static bool TryGetResponsesToolCallMapping(
        string toolName,
        IReadOnlyDictionary<string, ResponsesToolCallMapping>? mappings,
        out ResponsesToolCallMapping mapping)
    {
        if (mappings is not null
            && mappings.TryGetValue(toolName, out var exact))
        {
            mapping = exact;
            return true;
        }

        mapping = new ResponsesToolCallMapping();
        return false;
    }

    private static ResponsesToolCallShape FunctionToolShape(string name, string? namespaceName)
    {
        return new ResponsesToolCallShape
        {
            Kind = ResponsesToolCallKind.Function,
            ItemType = "function_call",
            ArgumentField = "arguments",
            Name = name,
            Namespace = namespaceName
        };
    }

    private static ResponsesToolCallShape CustomToolShape(string name, string? namespaceName)
    {
        return new ResponsesToolCallShape
        {
            Kind = ResponsesToolCallKind.CustomTool,
            ItemType = "custom_tool_call",
            ArgumentField = "input",
            Name = name,
            Namespace = namespaceName
        };
    }

    private static string NativeToolCallItemType(string nativeType)
    {
        if (nativeType is "custom" or "custom_tool")
        {
            return "custom_tool_call";
        }

        return nativeType.EndsWith("_call", StringComparison.Ordinal)
            ? nativeType
            : $"{nativeType}_call";
    }

    private static string NativeToolArgumentField(string nativeType)
    {
        // tool_search_call uses "arguments" (same as function_call), not "input"
        return nativeType == "tool_search" ? "arguments" : "input";
    }
}
