namespace OpenCodex.CoreBase.Abstractions;

/// <summary>以不依赖 Web 框架的方式写出原始代理响应。</summary>
public interface IProxyResponseBodyWriter
{
    Task WriteAsync(
        int statusCode,
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        Stream content,
        CancellationToken cancellationToken = default);
}
