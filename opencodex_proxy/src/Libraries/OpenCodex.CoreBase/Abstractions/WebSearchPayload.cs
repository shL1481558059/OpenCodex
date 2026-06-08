using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 提供用于转换和读取弱类型联网搜索载荷值的辅助方法。
/// </summary>
public static class WebSearchPayload
{
    /// <summary>
    /// 将载荷片段序列化回结构化文本时使用的序列化选项。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 将结构化文本元素转换为公共语言运行时基础值、字典和列表。
    /// </summary>
    /// <param name="element">要转换的 JSON 元素。</param>
    /// <returns>转换后的 CLR 值；当 JSON 为 null 或不支持的类型时返回 <see langword="null"/>。</returns>
    public static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    /// <summary>
    /// 创建字典载荷的深拷贝。
    /// </summary>
    /// <param name="value">要拷贝的字典。</param>
    /// <returns>包含已拷贝嵌套字典和列表的新字典。</returns>
    public static Dictionary<string, object?> DeepCopyObject(IReadOnlyDictionary<string, object?> value)
    {
        return value.ToDictionary(pair => pair.Key, pair => DeepCopy(pair.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// 当载荷值是受支持的集合类型时创建深拷贝。
    /// </summary>
    /// <param name="value">要拷贝的值。</param>
    /// <returns>字典和列表会返回拷贝值；其他类型返回原始值。</returns>
    public static object? DeepCopy(object? value)
    {
        return value switch
        {
            Dictionary<string, object?> dictionary => DeepCopyObject(dictionary),
            IReadOnlyDictionary<string, object?> dictionary => DeepCopyObject(dictionary),
            List<object?> list => list.Select(DeepCopy).ToList(),
            IReadOnlyList<object?> list => list.Select(DeepCopy).ToList(),
            _ => value
        };
    }

    /// <summary>
    /// 从字典中读取对象值。
    /// </summary>
    /// <param name="dictionary">包含目标值的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>对象值；当键不存在或值不是对象时返回空字典。</returns>
    public static Dictionary<string, object?> ObjectValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsObject(GetValue(dictionary, key), out var result) ? result : [];
    }

    /// <summary>
    /// 返回列表中第一个可解释为对象的元素。
    /// </summary>
    /// <param name="list">要扫描的列表。</param>
    /// <returns>第一个对象值；如果不存在则返回 <see langword="null"/>。</returns>
    public static Dictionary<string, object?>? FirstObject(List<object?> list)
    {
        foreach (var item in list)
        {
            if (TryAsObject(item, out var result))
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// 从字典中读取列表值。
    /// </summary>
    /// <param name="dictionary">包含目标值的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>列表值；当键不存在或值不是列表时返回空列表。</returns>
    public static List<object?> ListValue(Dictionary<string, object?> dictionary, string key)
    {
        return TryAsList(GetValue(dictionary, key), out var result) ? result : [];
    }

    /// <summary>
    /// 尝试将载荷值解释为对象字典。
    /// </summary>
    /// <param name="value">要检查的值。</param>
    /// <param name="dictionary">转换成功时得到的字典。</param>
    /// <returns>值可以作为对象读取时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            dictionary = readOnly.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return true;
        }

        dictionary = [];
        return false;
    }

    /// <summary>
    /// 尝试将载荷值解释为列表。
    /// </summary>
    /// <param name="value">要检查的值。</param>
    /// <param name="list">转换成功时得到的列表。</param>
    /// <returns>值可以作为列表读取时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typed)
        {
            list = typed;
            return true;
        }

        if (value is IReadOnlyList<object?> readOnly)
        {
            list = readOnly.ToList();
            return true;
        }

        list = [];
        return false;
    }

    /// <summary>
    /// 从字典中读取值，键不存在时不抛出异常。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>存储的值；当键不存在时返回 <see langword="null"/>。</returns>
    public static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// 将字典值读取为字符串。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <param name="fallback">键不存在或值为 null 时返回的值。</param>
    /// <returns>存储值的字符串表示；否则返回备用值。</returns>
    public static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key, string fallback = "")
    {
        return GetValue(dictionary, key)?.ToString() ?? fallback;
    }

    /// <summary>
    /// 将载荷值序列化为结构化文本。
    /// </summary>
    /// <param name="value">要序列化的值。</param>
    /// <returns>该值的 JSON 表示。</returns>
    public static string JsonDumps(object? value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    /// <summary>
    /// 在可能时将载荷值转换为整数。
    /// </summary>
    /// <param name="value">要转换的值。</param>
    /// <param name="fallback">无法转换时返回的备用值。</param>
    /// <returns>转换后的整数值；无法转换时返回备用值。</returns>
    public static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => fallback
        };
    }
}
