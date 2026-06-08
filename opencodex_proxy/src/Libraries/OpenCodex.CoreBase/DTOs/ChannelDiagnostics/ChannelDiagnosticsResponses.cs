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

/// <summary>
/// 表示通道连通性测试响应。
/// </summary>
public sealed class TestChannelResponse
{
    /// <summary>
    /// 初始化通道连通性测试响应。
    /// </summary>
    /// <param name="ok">测试是否成功。</param>
    /// <param name="durationMs">测试耗时毫秒数。</param>
    /// <param name="model">对外模型名称。</param>
    /// <param name="upstreamModel">上游模型名称。</param>
    /// <param name="compat">启用的兼容性选项。</param>
    /// <param name="response">上游响应内容。</param>
    public TestChannelResponse(
        bool ok,
        int durationMs,
        string model,
        string upstreamModel,
        IReadOnlyList<string> compat,
        IReadOnlyDictionary<string, object?> response)
    {
        Ok = ok;
        DurationMs = durationMs;
        Model = model;
        UpstreamModel = upstreamModel;
        Compat = compat;
        Response = response;
    }

    /// <summary>
    /// 获取测试是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; }

    /// <summary>
    /// 获取测试耗时毫秒数。
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    /// <summary>
    /// 获取对外模型名称。
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; }

    /// <summary>
    /// 获取上游模型名称。
    /// </summary>
    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; }

    /// <summary>
    /// 获取启用的兼容性选项。
    /// </summary>
    [JsonPropertyName("compat")]
    public IReadOnlyList<string> Compat { get; }

    /// <summary>
    /// 获取上游响应内容。
    /// </summary>
    [JsonPropertyName("response")]
    public IReadOnlyDictionary<string, object?> Response { get; }

    /// <summary>
    /// 根据通道测试结果创建成功响应。
    /// </summary>
    /// <param name="model">对外模型名称。</param>
    /// <param name="upstreamModel">上游模型名称。</param>
    /// <param name="compat">启用的兼容性选项。</param>
    /// <param name="response">上游响应内容。</param>
    /// <param name="durationMs">测试耗时毫秒数。</param>
    /// <returns>通道连通性测试响应。</returns>
    public static TestChannelResponse From(
        string model,
        string upstreamModel,
        IReadOnlyList<string> compat,
        IReadOnlyDictionary<string, object?> response,
        int durationMs)
    {
        return new TestChannelResponse(
            true,
            durationMs,
            model,
            upstreamModel,
            compat,
            response);
    }
}
