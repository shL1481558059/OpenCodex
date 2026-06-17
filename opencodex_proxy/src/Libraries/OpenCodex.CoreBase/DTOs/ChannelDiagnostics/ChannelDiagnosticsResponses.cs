using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.ChannelDiagnostics;

/// <summary>
/// 表示发现通道模型的响应。
/// </summary>
public sealed class DiscoverModelsResponse
{
    /// <summary>
    /// 初始化发现通道模型的响应。
    /// </summary>
    /// <param name="models">发现的模型名称列表。</param>
    /// <param name="raw">上游原始响应。</param>
    /// <param name="durationMs">诊断耗时毫秒数。</param>
    public DiscoverModelsResponse(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, object?> raw,
        int durationMs)
    {
        Models = models;
        Raw = raw;
        DurationMs = durationMs;
    }

    /// <summary>
    /// 获取发现的模型名称列表。
    /// </summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models { get; }

    /// <summary>
    /// 获取上游原始响应。
    /// </summary>
    [JsonPropertyName("raw")]
    public IReadOnlyDictionary<string, object?> Raw { get; }

    /// <summary>
    /// 获取诊断耗时毫秒数。
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    /// <summary>
    /// 根据发现结果创建响应对象。
    /// </summary>
    /// <param name="models">发现的模型名称列表。</param>
    /// <param name="raw">上游原始响应。</param>
    /// <param name="durationMs">诊断耗时毫秒数。</param>
    /// <returns>发现通道模型的响应。</returns>
    public static DiscoverModelsResponse From(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, object?> raw,
        int durationMs)
    {
        return new DiscoverModelsResponse(
            models,
            raw,
            durationMs);
    }
}

