namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyEndpointResult
{
    public ProxyEndpointResult(int StatusCode, object? Payload, bool IsEmpty)
    {
        this.StatusCode = StatusCode;
        this.Payload = Payload;
        this.IsEmpty = IsEmpty;
    }

    public int StatusCode { get; }

    public object? Payload { get; }

    public bool IsEmpty { get; }
}
