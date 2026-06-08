namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 包含代理服务所需的请求元数据。
/// </summary>
public sealed class ProxyRequestMetadata
{
    /// <summary>
    /// 初始化 <see cref="ProxyRequestMetadata"/> 类的新实例。
    /// </summary>
    /// <param name="method">传入请求的 HTTP 方法。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="clientIp">客户端 IP 地址（如果可用）。</param>
    /// <param name="headers">标准化后的请求头。</param>
    public ProxyRequestMetadata(
        string method,
        string path,
        string? clientIp,
        IReadOnlyDictionary<string, string> headers)
    {
        Method = method;
        Path = path;
        ClientIp = clientIp;
        Headers = headers;
    }

    /// <summary>
    /// 获取传入请求的超文本传输协议方法。
    /// </summary>
    public string Method { get; }

    /// <summary>
    /// 获取请求路径。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 获取客户端网络地址（如果可用）。
    /// </summary>
    public string? ClientIp { get; }

    /// <summary>
    /// 获取标准化后的请求头。
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}
