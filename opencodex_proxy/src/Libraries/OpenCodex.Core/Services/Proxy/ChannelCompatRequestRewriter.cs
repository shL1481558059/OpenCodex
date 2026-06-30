using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

public static class ChannelCompatRequestRewriter
{
    public static ChannelCompatRewriteResult Apply(
        IReadOnlyDictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> compat)
    {
        var result = WebSearchPayload.DeepCopyObject(payload);
        var details = new List<string>();

        // 1. default_params: 仅在参数不存在时添加
        foreach (var (key, value) in JsonDictionaryValue.Object(compat, "default_params", WebSearchPayload.DeepCopyObject))
        {
            if (!result.ContainsKey(key))
            {
                result[key] = CloneJsonValue(value);
                details.Add($"default:{key}");
            }
        }

        // 2. rename_params: 重命名参数
        foreach (var (source, targetValue) in JsonDictionaryValue.Object(compat, "rename_params", WebSearchPayload.DeepCopyObject))
        {
            var target = targetValue?.ToString() ?? string.Empty;
            if (target.Length == 0 || !result.ContainsKey(source))
            {
                continue;
            }

            if (!result.ContainsKey(target))
            {
                result[target] = CloneJsonValue(result[source]);
            }

            result.Remove(source);
            details.Add($"rename:{source}->{target}");
        }

        // 3. drop_params: 删除参数
        foreach (var item in JsonDictionaryValue.List(compat, "drop_params"))
        {
            var key = item?.ToString() ?? string.Empty;
            if (key.Length > 0 && result.Remove(key))
            {
                details.Add($"drop:{key}");
            }
        }

        // 4. force_params: 强制设置参数（覆盖已有值）
        foreach (var (key, value) in JsonDictionaryValue.Object(compat, "force_params", WebSearchPayload.DeepCopyObject))
        {
            result[key] = CloneJsonValue(value);
            details.Add($"force:{key}");
        }

        // 5. drop_tool_types: 删除特定类型的工具
        ApplyDropToolTypes(result, compat, details);

        // 6. unsupported_params: 检查不支持的参数并抛出异常
        var unsupported = JsonDictionaryValue.List(compat, "unsupported_params")
            .Select(item => item?.ToString() ?? string.Empty)
            .Where(key => key.Length > 0 && result.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new BadRequestException($"upstream does not support parameter(s): {string.Join(", ", unsupported)}");
        }


        // 7. preserve_thinking_history: inject internal marker for Messages protocol conversion
        var preserveThinkingHistory = JsonDictionaryValue.Get(compat, "preserve_thinking_history") is true;
        if (preserveThinkingHistory)
        {
            result["_ocxp_preserve_thinking_history"] = true;
            details.Add("preserve_thinking_history:true");
        }
        return new ChannelCompatRewriteResult(result, details);
    }

    private static object? CloneJsonValue(object? value)
    {
        return value switch
        {
            null => null,
            IReadOnlyDictionary<string, object?> dict => WebSearchPayload.DeepCopyObject(dict),
            IReadOnlyList<object?> list => list.Select(CloneJsonValue).ToList(),
            _ => value
        };
    }

    private static void ApplyDropToolTypes(
        Dictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> compat,
        List<string> details)
    {
        var dropToolTypes = JsonDictionaryValue.List(compat, "drop_tool_types")
            .Select(item => item?.ToString()?.Trim() ?? string.Empty)
            .Where(type => type.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (dropToolTypes.Count == 0)
        {
            return;
        }

        DropTools(payload, dropToolTypes, details);
        DropToolChoice(payload, dropToolTypes, details);
        DropIncludeItems(payload, dropToolTypes, details);
    }

    private static void DropTools(
        Dictionary<string, object?> payload,
        HashSet<string> dropToolTypes,
        List<string> details)
    {
        if (!WebSearchPayload.TryAsList(JsonDictionaryValue.Get(payload, "tools"), out var tools))
        {
            return;
        }

        var filtered = new List<object?>();
        foreach (var tool in tools)
        {
            if (WebSearchPayload.TryAsObject(tool, out var toolObject)
                && dropToolTypes.Contains(JsonDictionaryValue.String(toolObject, "type")))
            {
                details.Add($"drop_tool_type:{JsonDictionaryValue.String(toolObject, "type")}");
                continue;
            }

            filtered.Add(tool);
        }

        if (filtered.Count == tools.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("tools");
            return;
        }

        payload["tools"] = filtered;
    }

    private static void DropToolChoice(
        Dictionary<string, object?> payload,
        HashSet<string> dropToolTypes,
        List<string> details)
    {
        var toolChoice = JsonDictionaryValue.Get(payload, "tool_choice");
        var toolChoiceType = toolChoice switch
        {
            string text => text.Trim(),
            IReadOnlyDictionary<string, object?> obj => JsonDictionaryValue.String(obj, "type"),
            _ => string.Empty
        };
        if (toolChoiceType.Length == 0 || !dropToolTypes.Contains(toolChoiceType))
        {
            return;
        }

        payload.Remove("tool_choice");
        details.Add($"drop_tool_choice:{toolChoiceType}");
    }

    private static void DropIncludeItems(
        Dictionary<string, object?> payload,
        HashSet<string> dropToolTypes,
        List<string> details)
    {
        if (!WebSearchPayload.TryAsList(JsonDictionaryValue.Get(payload, "include"), out var includeItems))
        {
            return;
        }

        var filtered = new List<object?>();
        foreach (var item in includeItems)
        {
            if (item is string text && ContainsDroppedToolType(text, dropToolTypes))
            {
                details.Add($"drop_include:{text}");
                continue;
            }

            filtered.Add(item);
        }

        if (filtered.Count == includeItems.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("include");
            return;
        }

        payload["include"] = filtered;
    }

    private static bool ContainsDroppedToolType(string value, HashSet<string> dropToolTypes)
    {
        foreach (var toolType in dropToolTypes)
        {
            if (value.Contains(toolType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class ChannelCompatRewriteResult
{
    public ChannelCompatRewriteResult(Dictionary<string, object?> payload, IReadOnlyList<string> details)
    {
        Payload = payload;
        Details = details;
    }

    public Dictionary<string, object?> Payload { get; }

    public IReadOnlyList<string> Details { get; }
}

