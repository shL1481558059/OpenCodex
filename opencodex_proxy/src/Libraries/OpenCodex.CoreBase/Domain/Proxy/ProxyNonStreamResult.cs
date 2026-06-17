namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 表示非流式代理请求生成的响应。
/// </summary>
public sealed class ProxyNonStreamResult
{
    /// <summary>
    /// 初始化 <see cref="ProxyNonStreamResult"/> 类的新实例。
    /// </summary>
    /// <param name="statusCode">响应状态码。</param>
    /// <param name="payload">响应载荷（如果存在）。</param>
    /// <param name="failureException">导致该结果的失败异常（如果存在）。</param>
    public ProxyNonStreamResult(int statusCode, object? payload, Exception? failureException = null)
    {
        StatusCode = statusCode;
        Payload = payload;
        FailureException = failureException;
    }

    /// <summary>
    /// 获取响应状态码。
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// 获取响应载荷（如果存在）。
    /// </summary>
    public object? Payload { get; }

    /// <summary>
    /// 获取导致该结果的失败异常（如果存在）。
    /// </summary>
    public Exception? FailureException { get; }
}
