using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
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

        var routes = await _routes.ListVisionTransferRoutesAsync(context.OwnerUsername);
        var results = new List<ProxyOcrResult>(plan.UserImages.Count);
        foreach (var image in plan.UserImages.OrderBy(item => item.ImageNumber))
        {
            results.Add(await RecognizeWithFallbackAsync(context, routes, image));
        }

        var rewritten = _rewriter.ApplyOcrResults(plan, results);
        return new ProxyImageFallbackResult(rewritten, usedOcr: results.Count > 0);
    }

    /// <summary>
    /// 按主、兜底顺序尝试识别一张图片。上游类失败会把该路由记入请求级失败集合后换下一个候选;
    /// 配置类失败(没有候选)不重试,直接由 OCR 服务给出 400。attempt 反映本请求内该图片的累计尝试,
    /// 不因换候选或主请求换渠道重试而重置。
    /// </summary>
    private async Task<ProxyOcrResult> RecognizeWithFallbackAsync(
        ProxyImageFallbackContext context,
        VisionTransferRoutesDto routes,
        ProxyImageInput image)
    {
        if (routes.Candidates.Count == 0)
        {
            return await _ocr.RecognizeAsync(CreateOcrContext(
                context,
                image,
                visionRoute: null,
                ProxyVisionRouteKinds.None,
                attempt: ++context.RequestAttemptCount,
                routes.UnavailableReason));
        }

        UpstreamException? lastError = null;
        for (var index = 0; index < routes.Candidates.Count; index++)
        {
            var route = routes.Candidates[index];
            var routeKey = RouteKey(route);
            if (context.FailedVisionRoutes.Contains(routeKey))
            {
                continue;
            }

            try
            {
                return await _ocr.RecognizeAsync(CreateOcrContext(
                    context,
                    image,
                    route,
                    index == 0 ? ProxyVisionRouteKinds.Primary : ProxyVisionRouteKinds.Fallback,
                    ++context.RequestAttemptCount,
                    unavailableReason: string.Empty));
            }
            catch (UpstreamException exception)
            {
                context.FailedVisionRoutes.Add(routeKey);
                lastError = exception;
            }
        }

        throw lastError ?? new UpstreamException(
            "all configured vision transfer routes already failed in this request",
            ProxyHttpStatus.BadGateway,
            body: null);
    }

    private static ProxyOcrContext CreateOcrContext(
        ProxyImageFallbackContext context,
        ProxyImageInput image,
        ProxyRouteDto? visionRoute,
        string routeKind,
        int attempt,
        string unavailableReason)
    {
        return new ProxyOcrContext(
            context.RequestId,
            context.OwnerUsername,
            context.ApiKeyId,
            context.RequestMetadata,
            image,
            visionRoute,
            context.DefaultTimeout,
            context.CancellationToken,
            routeKind,
            attempt,
            unavailableReason);
    }

    private static string RouteKey(ProxyRouteDto route)
    {
        var channelId = route.Channel.TryGetValue("id", out var value)
            ? Convert.ToString(value) ?? string.Empty
            : string.Empty;
        return $"{channelId}/{route.UpstreamModel}";
    }
}
