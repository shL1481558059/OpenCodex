namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static List<object?> ResponsesToolsToCanonical(object? tools)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            result.AddRange(ResponsesToolToCanonicalItems(item));
        }

        return DedupeCanonicalTools(result);
    }

    private static List<object?> ChatToolsToCanonical(object? tools)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var function = GetString(tool, "type") == "function" ? ObjectValue(tool, "function") : tool;
            var entry = Obj(
                ("name", GetValue(function, "name")),
                ("description", GetValue(function, "description") ?? string.Empty),
                ("parameters", GetValue(function, "parameters") ?? new Dictionary<string, object?>()),
                ("native_type", "function"));
            if (GetValue(function, "name") is string name)
            {
                if (name.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
                {
                    entry["name"] = NamespaceNameToChat(name);
                    entry["namespace"] = name[..name.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal)];
                }
                else
                {
                    var (namespaceName, _) = NamespaceCallParts(name);
                    if (!string.IsNullOrEmpty(namespaceName))
                    {
                        entry["namespace"] = namespaceName;
                    }
                }
            }

            result.Add(entry);
        }

        return result;
    }

    private static List<object?> AnthropicToolsToCanonical(object? tools)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            result.Add(Obj(
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("parameters", GetValue(tool, "input_schema") ?? new Dictionary<string, object?>()),
                ("native_type", "function")));
        }

        return result;
    }

    private static List<object?> CanonicalToolsToResponses(List<object?> tools)
    {
        var result = new List<object?>();
        var namespaceGroups = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var nativeType = GetString(tool, "native_type") ?? "function";
            if (nativeType != "function" && TryAsObject(GetValue(tool, "raw"), out var raw) && raw.Count > 0)
            {
                result.Add(DeepCopy(raw));
                continue;
            }

            var namespaceName = GetString(tool, "namespace");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                var name = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
                var prefix = $"{namespaceName}{NamespaceSeparator}";
                var bareName = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
                if (bareName.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
                {
                    bareName = bareName[(bareName.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal) + 1)..];
                }

                if (!namespaceGroups.TryGetValue(namespaceName, out var group))
                {
                    group = [];
                    namespaceGroups[namespaceName] = group;
                }

                group.Add(Obj(
                    ("type", "function"),
                    ("name", bareName),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>())));
                continue;
            }

            result.Add(Obj(
                ("type", "function"),
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>())));
        }

        foreach (var (namespaceName, innerTools) in namespaceGroups)
        {
            result.Add(Obj(
                ("type", "namespace"),
                ("name", namespaceName),
                ("tools", innerTools)));
        }

        return result;
    }

    private static List<object?> CanonicalToolsToChat(List<object?> tools)
    {
        var result = new List<object?>();
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool) || !HasNonNullValue(tool, "name"))
            {
                continue;
            }

            result.Add(Obj(
                ("type", "function"),
                ("function", Obj(
                    ("name", NamespaceNameToChat(Convert.ToString(GetValue(tool, "name")) ?? string.Empty)),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", SanitizeToolSchema(GetValue(tool, "parameters") ?? new Dictionary<string, object?>()))))));
        }

        return result;
    }

    private static List<object?> CanonicalToolsToAnthropic(List<object?> tools)
    {
        var result = new List<object?>();
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool) || !HasNonNullValue(tool, "name"))
            {
                continue;
            }

            result.Add(Obj(
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("input_schema", SanitizeToolSchema(GetValue(tool, "parameters") ?? new Dictionary<string, object?>()))));
        }

        return result;
    }

    private static List<object?> ResponsesToolToCanonicalItems(object? item)
    {
        if (!TryAsObject(item, out var tool))
        {
            return [];
        }

        var toolType = GetString(tool, "type") ?? "function";
        if (toolType == "namespace")
        {
            var namespaceName = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
            var result = new List<object?>();
            foreach (var nested in ListValue(tool, "tools"))
            {
                foreach (var nestedItem in ResponsesToolToCanonicalItems(nested))
                {
                    if (!TryAsObject(nestedItem, out var nestedTool))
                    {
                        continue;
                    }

                    var bareName = Convert.ToString(GetValue(nestedTool, "name")) ?? string.Empty;
                    nestedTool["name"] = string.IsNullOrEmpty(namespaceName)
                        ? bareName
                        : $"{namespaceName}{NamespaceSeparator}{bareName}";
                    nestedTool["namespace"] = namespaceName;
                    result.Add(nestedTool);
                }
            }

            return result;
        }

        if (toolType == "function")
        {
            return
            [
                Obj(
                    ("name", GetValue(tool, "name")),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>()),
                    ("native_type", "function"))
            ];
        }

        if (toolType == "web_search")
        {
            return
            [
                Obj(
                    ("name", "web_search"),
                    ("description", GetValue(tool, "description") ?? "Search the web for current information."),
                    ("parameters", Obj(
                        ("type", "object"),
                        ("additionalProperties", false),
                        ("properties", Obj(
                            ("query", Obj(
                                ("type", "string"),
                                ("description", "The web search query."))))),
                        ("required", new List<object?> { "query" }))),
                    ("native_type", "web_search"),
                    ("raw", DeepCopy(tool)))
            ];
        }

        return [WrapNativeTool(toolType, tool)];
    }

    private static Dictionary<string, object?> WrapNativeTool(string toolType, Dictionary<string, object?> tool)
    {
        var name = Convert.ToString(GetValue(tool, "name"));
        if (string.IsNullOrEmpty(name))
        {
            name = toolType;
        }

        name = name.Replace("-", "_", StringComparison.Ordinal);

        var providedSchema = GetValue(tool, "parameters")
            ?? GetValue(tool, "input_schema")
            ?? GetValue(tool, "schema");

        Dictionary<string, object?> parameters;
        if (TryAsObject(providedSchema, out var explicitSchema) && explicitSchema.Count > 0)
        {
            parameters = explicitSchema;
        }
        else if (toolType is "local_shell" or "shell")
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("cmd", Obj(("type", "string"))))),
                ("required", new List<object?> { "cmd" }));
        }
        else if (toolType == "apply_patch")
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("patch", Obj(("type", "string"))))),
                ("required", new List<object?> { "patch" }));
        }
        else
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("input", Obj(("type", "string"))))));
        }

        return Obj(
            ("name", name),
            ("description", GetValue(tool, "description") ?? $"Wrapped Responses tool: {toolType}"),
            ("parameters", parameters),
            ("native_type", toolType),
            ("raw", DeepCopy(tool)));
    }

    private static List<object?> DedupeCanonicalTools(List<object?> tools)
    {
        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var nativeType = GetString(tool, "native_type") ?? "function";
            var name = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var scope = nativeType == "function" ? "function" : nativeType;
            if (!seen.Add($"{scope}\u001f{name}"))
            {
                continue;
            }

            result.Add(tool);
        }

        return result;
    }

    private static object? ToolChoiceToChat(object? toolChoice)
    {
        if (toolChoice is string)
        {
            return toolChoice;
        }

        if (TryAsObject(toolChoice, out var toolChoiceObject))
        {
            var function = ObjectValue(toolChoiceObject, "function");
            if (HasNonNullValue(function, "name"))
            {
                return toolChoice;
            }

            var type = GetString(toolChoiceObject, "type");
            if (type is "auto" or "none")
            {
                return type;
            }

            if (type is "required" or "tool" or "any" or "function")
            {
                return "required";
            }
        }

        return toolChoice;
    }
}
