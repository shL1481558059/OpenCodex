namespace OpenCodex.CoreBase.Services.Proxy;

public sealed class ProxyRequestState
{
    public ProxyRequestState(string requestId, string defaultOwnerUsername, int defaultTimeout)
    {
        RequestId = requestId;
        DefaultOwnerUsername = defaultOwnerUsername;
        DefaultTimeout = defaultTimeout;
    }

    public string RequestId { get; }

    public string DefaultOwnerUsername { get; }

    public int DefaultTimeout { get; }
}
