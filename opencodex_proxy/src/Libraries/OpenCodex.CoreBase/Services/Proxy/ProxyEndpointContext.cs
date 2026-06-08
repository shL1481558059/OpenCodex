using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyEndpointContext
{
    public ProxyEndpointContext(
        string entryProtocol,
        Dictionary<string, object?>? payload,
        string? authorizationHeader,
        ProxyRequestMetadata requestMetadata,
        IProxyStreamWriter streamWriter,
        CancellationToken cancellationToken)
    {
        EntryProtocol = entryProtocol;
        Payload = payload;
        AuthorizationHeader = authorizationHeader;
        RequestMetadata = requestMetadata;
        StreamWriter = streamWriter;
        CancellationToken = cancellationToken;
    }

    public string EntryProtocol { get; }

    public Dictionary<string, object?>? Payload { get; }

    public string? AuthorizationHeader { get; }

    public ProxyRequestMetadata RequestMetadata { get; }

    public IProxyStreamWriter StreamWriter { get; }

    public CancellationToken CancellationToken { get; }
}
