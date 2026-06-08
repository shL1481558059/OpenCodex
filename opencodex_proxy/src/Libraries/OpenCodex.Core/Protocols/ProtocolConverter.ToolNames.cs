namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static string NamespaceNameToChat(string name)
    {
        if (name.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
        {
            var splitAt = name.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal);
            return $"{name[..splitAt]}{NamespaceSeparator}{name[(splitAt + 1)..]}";
        }

        return name;
    }

    private static (string? Namespace, string BareName) NamespaceCallParts(object? name, object? namespaceValue = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;
        if (namespaceValue is not null)
        {
            var namespaceName = Convert.ToString(namespaceValue) ?? string.Empty;
            if (!string.IsNullOrEmpty(namespaceName))
            {
                var prefix = $"{namespaceName}{NamespaceSeparator}";
                return toolName.StartsWith(prefix, StringComparison.Ordinal)
                    ? (namespaceName, toolName[prefix.Length..])
                    : (namespaceName, toolName);
            }
        }

        if (toolName.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
        {
            var splitAt = toolName.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal);
            return (toolName[..splitAt], toolName[(splitAt + 1)..]);
        }

        var flat = SplitFlatNamespaceName(toolName);
        return flat ?? (null, toolName);
    }

    private static (string? Namespace, string BareName)? SplitFlatNamespaceName(string name)
    {
        if (!name.Contains(NamespaceSeparator, StringComparison.Ordinal))
        {
            return null;
        }

        int? splitAt = null;
        var start = 0;
        while (true)
        {
            var index = name.IndexOf(NamespaceSeparator, start, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            var namespacePart = name[..index];
            var bare = name[(index + NamespaceSeparator.Length)..];
            if (!string.IsNullOrEmpty(namespacePart) && !string.IsNullOrEmpty(bare) && !namespacePart.EndsWith('_'))
            {
                splitAt = index;
            }

            start = index + 1;
        }

        return splitAt is null
            ? null
            : (name[..splitAt.Value], name[(splitAt.Value + NamespaceSeparator.Length)..]);
    }

    private static Dictionary<string, object?> ResponsesFunctionCallNameFields(object? name, object? namespaceValue = null)
    {
        var (namespaceName, bareName) = NamespaceCallParts(name, namespaceValue);
        var result = Obj(("name", bareName));
        if (!string.IsNullOrEmpty(namespaceName))
        {
            result["namespace"] = namespaceName;
        }

        return result;
    }

    private static bool IsApplyPatchName(string name)
    {
        return name == "apply_patch" || name.EndsWith("/apply_patch", StringComparison.Ordinal);
    }
}
