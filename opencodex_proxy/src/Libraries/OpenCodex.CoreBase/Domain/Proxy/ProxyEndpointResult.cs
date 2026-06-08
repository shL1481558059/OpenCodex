namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 表示代理端点处理器返回的最终结果。
/// </summary>
public sealed class ProxyEndpointResult
{
    /// <summary>
    /// 初始化 <see cref="ProxyEndpointResult"/> 类的新实例。
    /// </summary>
    /// <param name="StatusCode">端点返回的超文本传输协议状态码。</param>
    /// <param name="Payload">端点响应载荷（如果存在）。</param>
    /// <param name="IsEmpty">指示响应是否没有载荷的值。</param>
    public ProxyEndpointResult(int StatusCode, object? Payload, bool IsEmpty)
    {
        this.StatusCode = StatusCode;
        this.Payload = Payload;
        this.IsEmpty = IsEmpty;
    }

    /// <summary>
    /// 获取端点返回的超文本传输协议状态码。
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// 获取端点响应载荷（如果存在）。
    /// </summary>
    public object? Payload { get; }

    /// <summary>
    /// 获取指示响应是否没有载荷的值。
    /// </summary>
    public bool IsEmpty { get; }
}
