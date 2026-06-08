using System.Text.Json;

namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 提供将结构化请求值规范化为公共语言运行时基础类型的辅助方法。
/// </summary>
public static class JsonRequestValue
{
    /// <summary>
    /// 将只读字典规范化为可变字典。
    /// </summary>
    /// <param name="source">源字典；为 <see langword="null"/> 时返回空字典。</param>
    /// <returns>规范化后的字典。</returns>
    public static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source.ToDictionary(
            pair => pair.Key,
            pair => Value(pair.Value),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 将枚举值规范化为列表。
    /// </summary>
    /// <param name="source">源枚举；为 <see langword="null"/> 时返回空列表。</param>
    /// <returns>规范化后的列表。</returns>
    public static List<object?> List(IEnumerable<object?>? source)
    {
        return source?.Select(Value).ToList() ?? [];
    }

    /// <summary>
    /// 将请求值规范化为公共语言运行时基础类型、字典或列表。
    /// </summary>
    /// <param name="value">要规范化的值。</param>
    /// <returns>规范化后的值。</returns>
    public static object? Value(object? value)
    {
        return value switch
        {
            JsonElement element => FromJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => Object(dictionary),
            IEnumerable<object?> values => values.Select(Value).ToList(),
            _ => value
        };
    }

    /// <summary>
    /// 将结构化文本元素转换为公共语言运行时基础类型、字典或列表。
    /// </summary>
    /// <param name="element">要转换的 JSON 元素。</param>
    /// <returns>转换后的值；当元素类型不可映射时返回 <see langword="null"/>。</returns>
    private static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NumberValue(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    /// <summary>
    /// 将结构化文本数值元素转换为整数、长整数或双精度数。
    /// </summary>
    /// <param name="element">要转换的 JSON 数值元素。</param>
    /// <returns>适合表示该数值的 CLR 数值类型。</returns>
    private static object NumberValue(JsonElement element)
    {
        if (!element.TryGetInt64(out var longValue))
        {
            return element.GetDouble();
        }

        if (longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return longValue;
    }
}
