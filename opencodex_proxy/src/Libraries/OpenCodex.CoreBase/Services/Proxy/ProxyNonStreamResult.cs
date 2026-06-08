namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyNonStreamResult
{
    public ProxyNonStreamResult(int statusCode, object? payload)
    {
        StatusCode = statusCode;
        Payload = payload;
    }

    public int StatusCode { get; }

    public object? Payload { get; }
}
