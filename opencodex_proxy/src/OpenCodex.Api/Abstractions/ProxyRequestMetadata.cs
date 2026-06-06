namespace OpenCodex.Api.Abstractions;

public sealed class ProxyRequestMetadata
{
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

    public string Method { get; }

    public string Path { get; }

    public string? ClientIp { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }
}
