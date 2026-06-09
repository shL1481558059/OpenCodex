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

    private static Dictionary<string, object?> SanitizeSchemaObject(Dictionary<string, object?> schema)
    {
        var result = schema.ToDictionary(
            pair => pair.Key,
            pair => SanitizeSchemaValue(pair.Value),
            StringComparer.Ordinal);

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
