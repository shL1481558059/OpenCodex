namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 定义可执行联网搜索提供方请求的客户端。
/// </summary>
public interface IWebSearchClient
{
    /// <summary>
    /// 使用指定的提供方密钥和查询执行联网搜索。
    /// </summary>
    /// <param name="key">要使用的提供方标识和 API 密钥。</param>
    /// <param name="query">搜索查询文本。</param>
    /// <param name="cancellationToken">用于取消搜索请求的令牌。</param>
    /// <returns>标准化后的提供方结果。</returns>
    Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken);
}

/// <summary>
/// 表示联网搜索提供方的名称和密钥组合。
/// </summary>
public sealed class WebSearchProviderKey
{
    /// <summary>
    /// 初始化 <see cref="WebSearchProviderKey"/> 类的新实例。
    /// </summary>
    /// <param name="provider">Web 搜索提供方名称。</param>
    /// <param name="key">提供方 API 密钥。</param>
    public WebSearchProviderKey(string provider, string key)
    {
        Provider = provider;
        Key = key;
    }

    /// <summary>
    /// 获取联网搜索提供方名称。
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// 获取提供方访问密钥。
    /// </summary>
    public string Key { get; }
}

/// <summary>
/// 表示联网搜索提供方返回的结果。
/// </summary>
public sealed class WebSearchProviderResult
{
    /// <summary>
    /// 初始化 <see cref="WebSearchProviderResult"/> 类的新实例。
    /// </summary>
    /// <param name="ok">指示提供方请求是否成功的值。</param>
    /// <param name="statusCode">提供方返回的 HTTP 状态码（如果可用）。</param>
    /// <param name="durationMs">提供方请求耗时，单位为毫秒。</param>
    /// <param name="errorType">标准化后的提供方错误类型（如果可用）。</param>
    /// <param name="error">提供方错误消息（如果可用）。</param>
    /// <param name="summary">标准化后的搜索摘要。</param>
    /// <param name="raw">原始提供方响应（如果可用）。</param>
    public WebSearchProviderResult(
        bool ok,
        int? statusCode,
        int durationMs,
        string? errorType,
        string? error,
        WebSearchSummary summary,
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
    /// 获取指示提供方请求是否成功的值。
    /// </summary>
    public bool Ok { get; }

    /// <summary>
    /// 获取提供方返回的超文本传输协议状态码（如果可用）。
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// 获取提供方请求耗时，单位为毫秒。
    /// </summary>
    public int DurationMs { get; }

    /// <summary>
    /// 获取标准化后的提供方错误类型（如果可用）。
    /// </summary>
    public string? ErrorType { get; }

    /// <summary>
    /// 获取提供方错误消息（如果可用）。
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// 获取标准化后的搜索摘要。
    /// </summary>
    public WebSearchSummary Summary { get; }

    /// <summary>
    /// 获取原始提供方响应（如果可用）。
    /// </summary>
    public object? Raw { get; }
}

/// <summary>
/// 表示联网搜索响应的标准化摘要。
/// </summary>
public sealed class WebSearchSummary
{
    /// <summary>
    /// 初始化 <see cref="WebSearchSummary"/> 类的新实例。
    /// </summary>
    /// <param name="answer">摘要答案文本。</param>
    /// <param name="results">标准化后的搜索结果项。</param>
    /// <param name="error">摘要级错误消息（如果可用）。</param>
    public WebSearchSummary(
        string answer,
        IReadOnlyList<Dictionary<string, object?>> results,
        string? error)
    {
        Answer = answer;
        Results = results;
        Error = error;
    }

    /// <summary>
    /// 获取摘要答案文本。
    /// </summary>
    public string Answer { get; }

    /// <summary>
    /// 获取标准化后的搜索结果项。
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Results { get; }

    /// <summary>
    /// 获取摘要级错误消息（如果可用）。
    /// </summary>
    public string? Error { get; }
}
