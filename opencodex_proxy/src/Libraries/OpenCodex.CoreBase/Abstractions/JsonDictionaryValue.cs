namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 提供用于弱类型结构化字典的类型化访问器。
/// </summary>
public static class JsonDictionaryValue
{
    /// <summary>
    /// 按键从字典中读取值。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>存储的值；当键不存在时返回 <see langword="null"/>。</returns>
    public static object? Get(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// 从字典中读取去除首尾空白后的字符串值。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>去除首尾空白后的字符串值；当键不存在或值为 null 时返回空字符串。</returns>
    public static string String(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return (Get(dictionary, key)?.ToString() ?? string.Empty).Trim();
    }

    /// <summary>
    /// 从字典中读取列表值。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <returns>列表值；当键不存在或值不可枚举时返回空列表。</returns>
    public static List<object?> List(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return Get(dictionary, key) switch
        {
            List<object?> list => list,
            IEnumerable<object?> values => values.ToList(),
            _ => []
        };
    }

    /// <summary>
    /// 从字典中读取对象值并进行克隆。
    /// </summary>
    /// <param name="dictionary">要读取的字典。</param>
    /// <param name="key">要读取的键。</param>
    /// <param name="clone">用于克隆匹配对象值的函数。</param>
    /// <returns>克隆后的对象字典；当键不存在或值不是对象时返回空字典。</returns>
    public static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?> dictionary,
        string key,
        Func<IReadOnlyDictionary<string, object?>, Dictionary<string, object?>> clone)
    {
        return Get(dictionary, key) is IReadOnlyDictionary<string, object?> value
            ? clone(value)
            : [];
    }
}
