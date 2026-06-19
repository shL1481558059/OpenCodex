namespace OpenCodex.Core.Protocols;

internal enum ResponsesToolCallKind
{
    Function,
    CustomTool
}

public static partial class ProtocolConverter
{
    internal static ResponsesToolCallKind GetResponsesToolCallKind(object? name)
    {
        var normalized = (Convert.ToString(name) ?? string.Empty)
            .Replace("-", "_", StringComparison.Ordinal);
        return IsApplyPatchName(normalized)
            ? ResponsesToolCallKind.CustomTool
            : ResponsesToolCallKind.Function;
    }
}
