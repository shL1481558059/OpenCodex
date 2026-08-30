namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static readonly string[] SchemaCompositionKeys =
    [
        "anyOf",
        "oneOf",
        "allOf",
        "any_of",
        "one_of",
        "all_of"
    ];

    private static void SanitizeRequestToolSchemas(Dictionary<string, object?> request, string targetProtocol)
    {
        if (targetProtocol == Chat)
        {
            SanitizeChatRequestToolSchemas(request);
        }
        else if (targetProtocol == Messages)
        {
            SanitizeMessagesRequestToolSchemas(request);
        }
    }

    /// <summary>
    /// Upstream providers reject requests that set tool_choice without any tools.
    /// Keep the outbound request consistent after conversion and same-protocol passthrough.
    /// </summary>
    private static void SanitizeRequestToolChoiceConsistency(Dictionary<string, object?> request)
    {
        if (HasEffectiveTools(request))
        {
            return;
        }

        if (request.TryGetValue("tools", out var tools)
            && tools is IReadOnlyList<object?> toolList
            && toolList.Count == 0)
        {
            request.Remove("tools");
        }

        request.Remove("tool_choice");
    }

    private static bool HasEffectiveTools(Dictionary<string, object?> request)
    {
        if (ListValue(request, "tools").Count > 0)
        {
            return true;
        }

        // Anthropic Messages may advertise remote MCP servers without a local tools array.
        return ListValue(request, "mcp_servers").Count > 0;
    }

    private static void SanitizeChatRequestToolSchemas(Dictionary<string, object?> request)
    {
        var tools = ListValue(request, "tools");
        if (tools.Count == 0)
        {
            return;
        }

        request["tools"] = tools.Select(SanitizeChatToolSchema).ToList();
    }

    private static void SanitizeMessagesRequestToolSchemas(Dictionary<string, object?> request)
    {
        var tools = ListValue(request, "tools");
        if (tools.Count == 0)
        {
            return;
        }

        request["tools"] = tools.Select(SanitizeMessagesToolSchema).ToList();
    }

    private static object? SanitizeChatToolSchema(object? item)
    {
        if (!TryAsObject(item, out var tool))
        {
            return DeepCopy(item);
        }

        var result = tool.ToDictionary(
            pair => pair.Key,
            pair => DeepCopy(pair.Value),
            StringComparer.Ordinal);
        if (TryAsObject(GetValue(result, "function"), out var function) && function.Count > 0)
        {
            function = function.ToDictionary(
                pair => pair.Key,
                pair => DeepCopy(pair.Value),
                StringComparer.Ordinal);
            if (function.TryGetValue("parameters", out var parameters))
            {
                function["parameters"] = SanitizeToolSchema(parameters);
            }

            result["function"] = function;
        }
        else if (result.TryGetValue("parameters", out var parameters))
        {
            result["parameters"] = SanitizeToolSchema(parameters);
        }

        return result;
    }

    private static object? SanitizeMessagesToolSchema(object? item)
    {
        if (!TryAsObject(item, out var tool))
        {
            return DeepCopy(item);
        }

        var result = tool.ToDictionary(
            pair => pair.Key,
            pair => DeepCopy(pair.Value),
            StringComparer.Ordinal);
        if (result.TryGetValue("input_schema", out var inputSchema))
        {
            result["input_schema"] = SanitizeToolSchema(inputSchema);
        }

        return result;
    }

    private static object? SanitizeToolSchema(object? schema)
    {
        schema = NormalizeJsonValue(schema);
        if (TryAsObject(schema, out var root))
        {
            schema = ExpandSchemaDefs(root, root, depth: 0);
        }

        return SanitizeSchemaValue(schema);
    }

    private static object? SanitizeSchemaValue(object? value)
    {
        value = NormalizeJsonValue(value);
        if (TryAsObject(value, out var dictionary))
        {
            return SanitizeSchemaObject(dictionary);
        }

        if (TryAsList(value, out var list))
        {
            return list.Select(SanitizeSchemaValue).ToList();
        }

        return value;
    }

    private const int MaxSchemaDefDepth = 32;

    /// <summary>
    /// 把工具参数 schema 里的 $ref/$defs 内部引用就地展开为自包含结构。
    /// chat/messages 上游（含部分中转聚合层）不支持引用式 schema，遇到 $ref 会拒绝。
    /// </summary>
    private static Dictionary<string, object?> ExpandSchemaDefs(
        Dictionary<string, object?> schema,
        Dictionary<string, object?> root,
        int depth)
    {
        if (depth > MaxSchemaDefDepth)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>()
            };
        }

        var defs = TryAsObject(GetValue(root, "$defs"), out var defsObject)
            ? defsObject
            : new Dictionary<string, object?>();

        var result = schema.ToDictionary(
            pair => pair.Key,
            pair => ExpandSchemaDefsValue(pair.Value, root, defs, depth),
            StringComparer.Ordinal);

        result.Remove("$defs");

        if (TryGetSchemaRef(result, out var refKey))
        {
            result.Remove("$ref");
            if (defs.TryGetValue(refKey, out var definition))
            {
                var expanded = ExpandSchemaDefsValue(definition, root, defs, depth + 1);
                if (TryAsObject(expanded, out var expandedObject))
                {
                    foreach (var (key, value) in expandedObject)
                    {
                        if (!result.ContainsKey(key))
                        {
                            result[key] = value;
                        }
                    }
                }
            }
        }

        return result;
    }

    private static object? ExpandSchemaDefsValue(
        object? value,
        Dictionary<string, object?> root,
        Dictionary<string, object?> defs,
        int depth)
    {
        if (TryAsObject(value, out var dictionary))
        {
            return ExpandSchemaDefs(dictionary, root, depth);
        }

        if (TryAsList(value, out var list))
        {
            return list
                .Select(item => ExpandSchemaDefsValue(item, root, defs, depth))
                .ToList();
        }

        return DeepCopy(value);
    }

    private static bool TryGetSchemaRef(Dictionary<string, object?> schema, out string refKey)
    {
        if (schema.TryGetValue("$ref", out var refValue)
            && NormalizeJsonValue(refValue) is string refText
            && refText.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            refKey = refText["#/$defs/".Length..];
            return true;
        }

        refKey = string.Empty;
        return false;
    }

    private static Dictionary<string, object?> SanitizeSchemaObject(Dictionary<string, object?> schema)
    {
        var result = schema.ToDictionary(
            pair => pair.Key,
            pair => SanitizeSchemaValue(pair.Value),
            StringComparer.Ordinal);

        // 部分上游（如某些中转聚合层）要求 object 类型的工具参数 schema 必须显式携带
        // required 字段，缺失时直接返回 400。这里统一补齐，避免 Codex 下发无 required
        // 的工具（例如 list_mcp_resources）导致上游拒绝。
        if (NormalizeJsonValue(GetValue(result, "type")) as string == "object"
            && !result.ContainsKey("required"))
        {
            result["required"] = new List<object?>();
        }

        SanitizeEnum(result);
        SanitizeCompositionSchemas(result);
        return result;
    }

    private static void SanitizeEnum(Dictionary<string, object?> schema)
    {
        if (!schema.TryGetValue("enum", out var enumValue)
            || !TryAsList(enumValue, out var values)
            || !values.Any(IsEmptyString))
        {
            return;
        }

        var filtered = values
            .Where(value => !IsEmptyString(value))
            .Select(DeepCopy)
            .ToList();
        if (filtered.Count > 0)
        {
            schema["enum"] = filtered;
            return;
        }

        schema.Remove("enum");
        if (!schema.ContainsKey("type") && InferJsonSchemaType(values) is { } inferredType)
        {
            schema["type"] = inferredType;
        }
    }

    private static void SanitizeCompositionSchemas(Dictionary<string, object?> schema)
    {
        foreach (var key in SchemaCompositionKeys)
        {
            if (!schema.TryGetValue(key, out var value) || !TryAsList(value, out var variants))
            {
                continue;
            }

            var sanitized = DedupeSchemaVariants(variants
                .Select(SanitizeSchemaValue)
                .Where(variant => variant is not null)
                .ToList());

            if (sanitized.Count == 0)
            {
                schema.Remove(key);
            }
            else if (sanitized.Count == 1 && TryAsObject(sanitized[0], out var onlyVariant))
            {
                schema.Remove(key);
                foreach (var (variantKey, variantValue) in onlyVariant)
                {
                    if (!schema.ContainsKey(variantKey))
                    {
                        schema[variantKey] = DeepCopy(variantValue);
                    }
                }
            }
            else
            {
                schema[key] = sanitized;
            }
        }
    }

    private static List<object?> DedupeSchemaVariants(List<object?> variants)
    {
        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var key = JsonDumps(variant);
            if (seen.Add(key))
            {
                result.Add(variant);
            }
        }

        return result;
    }

    private static bool IsEmptyString(object? value)
    {
        return NormalizeJsonValue(value) is string text && text.Length == 0;
    }

    private static string? InferJsonSchemaType(List<object?> enumValues)
    {
        foreach (var value in enumValues.Select(NormalizeJsonValue))
        {
            if (value is string)
            {
                return "string";
            }

            if (value is bool)
            {
                return "boolean";
            }

            if (value is int or long or double or decimal)
            {
                return "number";
            }
        }

        return null;
    }
}
