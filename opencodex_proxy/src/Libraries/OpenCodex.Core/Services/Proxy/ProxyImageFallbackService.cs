using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyImageFallbackService : IProxyImageFallbackService
{
    private readonly IProxyImagePayloadRewriter _rewriter;
    private readonly IProxyOcrService _ocr;
    private readonly IProxyRouteService _routes;

    public ProxyImageFallbackService(
        IProxyImagePayloadRewriter rewriter,
        IProxyOcrService ocr,
        IProxyRouteService routes)
    {
        _rewriter = rewriter;
        _ocr = ocr;
        _routes = routes;
    }

    public async Task<ProxyImageFallbackResult> RewriteAsync(ProxyImageFallbackContext context)
    {
        var plan = _rewriter.Prepare(context.Payload, context.EntryProtocol);
        if (plan.UserImages.Count == 0)
        {
            return new ProxyImageFallbackResult(plan.Payload, usedOcr: false);
        }

        var visionRoute = _routes.ChooseOcrRoute(context.OwnerUsername, context.RequestModel);
        var results = new List<ProxyOcrResult>(plan.UserImages.Count);
        foreach (var image in plan.UserImages.OrderBy(item => item.ImageNumber))
        {
            results.Add(await _ocr.RecognizeAsync(new ProxyOcrContext(
                context.RequestId,
                context.OwnerUsername,
                context.ApiKeyId,
                context.RequestMetadata,
                image,
                visionRoute,
                context.DefaultTimeout,
                context.CancellationToken)));
        }

        var rewritten = _rewriter.ApplyOcrResults(plan, results);
        return new ProxyImageFallbackResult(rewritten, usedOcr: results.Count > 0);
    }
}
