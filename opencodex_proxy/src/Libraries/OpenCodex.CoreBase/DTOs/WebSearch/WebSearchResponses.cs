using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.CoreBase.DTOs.WebSearch;

/// <summary>
/// 表示后台联网搜索配置响应。
/// </summary>
public sealed class WebSearchConfigResponse
{
    /// <summary>
    /// 初始化后台联网搜索配置响应。
    /// </summary>
    /// <param name="enabled">联网搜索是否启用。</param>
    /// <param name="providers">可用搜索提供方列表。</param>
    /// <param name="defaultKeyUsageLimit">默认密钥使用次数上限。</param>
    /// <param name="keys">密钥响应列表。</param>
    public WebSearchConfigResponse(
        bool enabled,
        IReadOnlyList<string> providers,
        int defaultKeyUsageLimit,
        IReadOnlyList<TavilyKeyResponse> keys)
    {
        Enabled = enabled;
        Providers = providers;
        DefaultKeyUsageLimit = defaultKeyUsageLimit;
        Keys = keys;
    }

    /// <summary>
    /// 获取联网搜索是否启用。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    /// <summary>
    /// 获取可用搜索提供方列表。
    /// </summary>
    [JsonPropertyName("providers")]
    public IReadOnlyList<string> Providers { get; }

    /// <summary>
    /// 获取默认密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("default_key_usage_limit")]
    public int DefaultKeyUsageLimit { get; }

    /// <summary>
    /// 获取搜索密钥响应列表。
    /// </summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<TavilyKeyResponse> Keys { get; }

    /// <summary>
    /// 根据配置数据创建后台联网搜索配置响应。
    /// </summary>
    /// <param name="config">配置数据。</param>
    /// <returns>后台联网搜索配置响应。</returns>
    public static WebSearchConfigResponse From(WebSearchConfigDto config)
    {
        return new WebSearchConfigResponse(
            config.Enabled,
            config.Providers,
            config.DefaultKeyUsageLimit,
            config.Keys.Select(TavilyKeyResponse.From).ToList());
    }
}

/// <summary>
/// 表示单个联网搜索密钥响应。
/// </summary>
public sealed class TavilyKeyResponse
{
    /// <summary>
    /// 初始化单个联网搜索密钥响应。
    /// </summary>
    /// <param name="id">密钥标识。</param>
    /// <param name="position">密钥排序位置。</param>
    /// <param name="provider">搜索提供方。</param>
    /// <param name="key">密钥原文。</param>
    /// <param name="enabled">密钥是否启用。</param>
    /// <param name="usageCount">已使用次数。</param>
    /// <param name="usageLimit">使用次数上限。</param>
    /// <param name="keyUsageLimit">兼容字段中的使用次数上限。</param>
    public TavilyKeyResponse(
        long id,
        int position,
        string provider,
        string key,
        bool enabled,
        int usageCount,
        int usageLimit,
        int keyUsageLimit)
    {
        Id = id;
        Position = position;
        Provider = provider;
        Key = key;
        Enabled = enabled;
        UsageCount = usageCount;
        UsageLimit = usageLimit;
        KeyUsageLimit = keyUsageLimit;
    }

    /// <summary>
    /// 获取密钥标识。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取密钥排序位置。
    /// </summary>
    [JsonPropertyName("position")]
    public int Position { get; }

    /// <summary>
    /// 获取搜索提供方。
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; }

    /// <summary>
    /// 获取密钥原文。
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; }

    /// <summary>
    /// 获取密钥是否启用。
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    /// <summary>
    /// 获取密钥已使用次数。
    /// </summary>
    [JsonPropertyName("usage_count")]
    public int UsageCount { get; }

    /// <summary>
    /// 获取密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("usage_limit")]
    public int UsageLimit { get; }

    /// <summary>
    /// 获取兼容字段中的密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("key_usage_limit")]
    public int KeyUsageLimit { get; }

    /// <summary>
    /// 根据密钥数据创建响应对象。
    /// </summary>
    /// <param name="key">密钥数据。</param>
    /// <returns>单个联网搜索密钥响应。</returns>
    public static TavilyKeyResponse From(TavilyKeyDto key)
    {
        return new TavilyKeyResponse(
            key.Id,
            key.Position,
            key.Provider,
            key.Key,
            key.Enabled,
            key.UsageCount,
            key.UsageLimit,
            key.KeyUsageLimit);
    }
}

/// <summary>
/// 表示测试联网搜索密钥时返回的密钥信息。
/// </summary>
public sealed class WebSearchTestKeyResponse
{
    /// <summary>
    /// 初始化测试联网搜索密钥时返回的密钥信息。
    /// </summary>
    /// <param name="id">密钥标识。</param>
    /// <param name="provider">搜索提供方。</param>
    /// <param name="usageCount">已使用次数。</param>
    /// <param name="usageLimit">使用次数上限。</param>
    /// <param name="keyUsageLimit">兼容字段中的使用次数上限。</param>
    public WebSearchTestKeyResponse(
        long id,
        string provider,
        int usageCount,
        int usageLimit,
        int keyUsageLimit)
    {
        Id = id;
        Provider = provider;
        UsageCount = usageCount;
        UsageLimit = usageLimit;
        KeyUsageLimit = keyUsageLimit;
    }

    /// <summary>
    /// 获取密钥标识。
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; }

    /// <summary>
    /// 获取搜索提供方。
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; }

    /// <summary>
    /// 获取密钥已使用次数。
    /// </summary>
    [JsonPropertyName("usage_count")]
    public int UsageCount { get; }

    /// <summary>
    /// 获取密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("usage_limit")]
    public int UsageLimit { get; }

    /// <summary>
    /// 获取兼容字段中的密钥使用次数上限。
    /// </summary>
    [JsonPropertyName("key_usage_limit")]
    public int KeyUsageLimit { get; }

    /// <summary>
    /// 根据密钥数据创建测试响应中的密钥信息。
    /// </summary>
    /// <param name="key">密钥数据。</param>
    /// <returns>测试响应中的密钥信息。</returns>
    public static WebSearchTestKeyResponse From(TavilyKeyDto key)
    {
        return new WebSearchTestKeyResponse(
            key.Id,
            key.Provider,
            key.UsageCount,
            key.UsageLimit,
            key.KeyUsageLimit);
    }
}

/// <summary>
/// 表示单次联网搜索提供方调用结果响应。
/// </summary>
public sealed class WebSearchProviderResultResponse
{
    /// <summary>
    /// 初始化单次联网搜索提供方调用结果响应。
    /// </summary>
    /// <param name="ok">调用是否成功。</param>
    /// <param name="statusCode">上游状态码。</param>
    /// <param name="durationMs">调用耗时毫秒数。</param>
    /// <param name="errorType">错误类型。</param>
    /// <param name="error">错误消息。</param>
    /// <param name="summary">搜索摘要。</param>
    /// <param name="raw">原始响应内容。</param>
    public WebSearchProviderResultResponse(
        bool ok,
        int? statusCode,
        int durationMs,
        string? errorType,
        string? error,
        WebSearchSummaryResponse summary,
        object? raw)
    {
        Ok = ok;
        StatusCode = statusCode;
        DurationMs = durationMs;
        ErrorType = errorType;
        Error = error;
        Summary = summary;
        Raw = raw;
    }

    /// <summary>
    /// 获取调用是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; }

    /// <summary>
    /// 获取上游状态码。
    /// </summary>
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; }

    /// <summary>
    /// 获取调用耗时毫秒数。
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; }

    /// <summary>
    /// 获取错误类型。
    /// </summary>
    [JsonPropertyName("error_type")]
    public string? ErrorType { get; }

    /// <summary>
    /// 获取错误消息。
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; }

    /// <summary>
    /// 获取搜索摘要。
    /// </summary>
    [JsonPropertyName("summary")]
    public WebSearchSummaryResponse Summary { get; }

    /// <summary>
    /// 获取原始响应内容。
    /// </summary>
    [JsonPropertyName("raw")]
    public object? Raw { get; }

    /// <summary>
    /// 根据提供方调用结果创建响应对象。
    /// </summary>
    /// <param name="result">提供方调用结果。</param>
    /// <returns>联网搜索提供方调用结果响应。</returns>
    public static WebSearchProviderResultResponse From(WebSearchProviderResult result)
    {
        return new WebSearchProviderResultResponse(
            result.Ok,
            result.StatusCode,
            result.DurationMs,
            result.ErrorType,
            result.Error,
            WebSearchSummaryResponse.From(result.Summary),
            result.Raw);
    }
}

/// <summary>
/// 表示联网搜索结果摘要响应。
/// </summary>
public sealed class WebSearchSummaryResponse
{
    /// <summary>
    /// 初始化联网搜索结果摘要响应。
    /// </summary>
    /// <param name="answer">摘要答案。</param>
    /// <param name="results">搜索结果列表。</param>
    /// <param name="error">摘要错误消息。</param>
    public WebSearchSummaryResponse(
        string answer,
        IReadOnlyList<object?> results,
        string? error)
    {
        Answer = answer;
        Results = results;
        Error = error;
    }

    /// <summary>
    /// 获取摘要答案。
    /// </summary>
    [JsonPropertyName("answer")]
    public string Answer { get; }

    /// <summary>
    /// 获取搜索结果列表。
    /// </summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<object?> Results { get; }

    /// <summary>
    /// 获取摘要错误消息。
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; }

    /// <summary>
    /// 根据搜索摘要数据创建响应对象。
    /// </summary>
    /// <param name="summary">搜索摘要数据。</param>
    /// <returns>联网搜索结果摘要响应。</returns>
    public static WebSearchSummaryResponse From(WebSearchSummary summary)
    {
        return new WebSearchSummaryResponse(
            summary.Answer,
            summary.Results.Select(item => (object?)item).ToList(),
            summary.Error);
    }
}

/// <summary>
/// 表示后台测试联网搜索密钥的完整响应。
/// </summary>
public sealed class WebSearchTestKeyResponsePayload
{
    /// <summary>
    /// 初始化后台测试联网搜索密钥的完整响应。
    /// </summary>
    /// <param name="ok">测试是否成功。</param>
    /// <param name="durationMs">测试耗时毫秒数。</param>
    /// <param name="key">被测试的密钥信息。</param>
    /// <param name="result">提供方调用结果。</param>
    /// <param name="config">测试后的联网搜索配置。</param>
    public WebSearchTestKeyResponsePayload(
        bool ok,
        long durationMs,
        WebSearchTestKeyResponse key,
        WebSearchProviderResultResponse result,
        WebSearchConfigResponse config)
    {
        Ok = ok;
        DurationMs = durationMs;
        Key = key;
        Result = result;
        Config = config;
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
    public long DurationMs { get; }

    /// <summary>
    /// 获取被测试的密钥信息。
    /// </summary>
    [JsonPropertyName("key")]
    public WebSearchTestKeyResponse Key { get; }

    /// <summary>
    /// 获取提供方调用结果。
    /// </summary>
    [JsonPropertyName("result")]
    public WebSearchProviderResultResponse Result { get; }

    /// <summary>
    /// 获取测试后的联网搜索配置。
    /// </summary>
    [JsonPropertyName("config")]
    public WebSearchConfigResponse Config { get; }

    /// <summary>
    /// 根据测试过程数据创建完整响应。
    /// </summary>
    /// <param name="key">被测试的密钥数据。</param>
    /// <param name="result">提供方调用结果。</param>
    /// <param name="config">测试后的配置数据。</param>
    /// <param name="durationMs">测试耗时毫秒数。</param>
    /// <returns>后台测试联网搜索密钥的完整响应。</returns>
    public static WebSearchTestKeyResponsePayload From(
        TavilyKeyDto key,
        WebSearchProviderResult result,
        WebSearchConfigDto config,
        long durationMs)
    {
        return new WebSearchTestKeyResponsePayload(
            result.Ok,
            durationMs,
            WebSearchTestKeyResponse.From(key),
            WebSearchProviderResultResponse.From(result),
            WebSearchConfigResponse.From(config));
    }
}
