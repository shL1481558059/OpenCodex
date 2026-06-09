using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyImagePayloadRewriter
{
    ProxyImagePayloadRewritePlan Prepare(
        Dictionary<string, object?> payload,
        string entryProtocol);

    Dictionary<string, object?> ApplyOcrResults(
        ProxyImagePayloadRewritePlan plan,
        IReadOnlyList<ProxyOcrResult> results);
}
