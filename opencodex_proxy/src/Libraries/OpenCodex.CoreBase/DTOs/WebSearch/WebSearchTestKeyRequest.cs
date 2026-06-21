using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.WebSearch;

/// <summary>
/// 表示后台测试联网搜索密钥时提交的请求。
/// </summary>
public sealed class WebSearchTestKeyRequest
{
    /// <summary>
    /// 获取或设置要测试的密钥标识。
    /// </summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    /// <summary>
    /// 获取或设置测试搜索词。
    /// </summary>
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// 获取实际用于测试的搜索词。
    /// </summary>
    /// <returns>去除空白后的搜索词，空值时返回默认搜索词。</returns>
    public string EffectiveQuery()
    {
        var query = Query.Trim();
        return query.Length == 0 ? "OpenAI" : query;
    }
}
