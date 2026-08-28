using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 覆盖 OCR 兜底编排:候选顺序、主失败后转兜底、请求级失败记忆与异常语义。
/// </summary>
public sealed class ProxyVisionTransferFallbackTests
{
    [Fact]
    public async Task PrimarySucceeds_OnlyPrimaryIsCalled()
    {
        var ocr = new StubOcrService();
        var service = CreateService(ocr, TwoCandidates());

        var result = await service.RewriteAsync(CreateContext(imageCount: 1));

        Assert.True(result.UsedOcr);
        var call = Assert.Single(ocr.Calls);
        Assert.Equal(ProxyVisionRouteKinds.Primary, call.RouteKind);
        Assert.Equal(1, call.Attempt);
        Assert.Equal("primary-upstream", call.VisionRoute!.UpstreamModel);
    }

    [Fact]
    public async Task PrimaryFailsUpstream_FallbackTakesOver()
    {
        var ocr = new StubOcrService { FailingUpstreamModels = { "primary-upstream" } };
        var service = CreateService(ocr, TwoCandidates());

        var result = await service.RewriteAsync(CreateContext(imageCount: 1));

        Assert.True(result.UsedOcr);
        Assert.Equal(2, ocr.Calls.Count);
        Assert.Equal(ProxyVisionRouteKinds.Primary, ocr.Calls[0].RouteKind);
        Assert.Equal(1, ocr.Calls[0].Attempt);
        Assert.Equal(ProxyVisionRouteKinds.Fallback, ocr.Calls[1].RouteKind);
        Assert.Equal(2, ocr.Calls[1].Attempt);
        Assert.Equal("fallback-upstream", ocr.Calls[1].VisionRoute!.UpstreamModel);
    }

    [Fact]
    public async Task BothRoutesFail_ThrowsLastUpstreamError()
    {
        var ocr = new StubOcrService { FailingUpstreamModels = { "primary-upstream", "fallback-upstream" } };
        var service = CreateService(ocr, TwoCandidates());

        var exception = await Assert.ThrowsAsync<UpstreamException>(
            () => service.RewriteAsync(CreateContext(imageCount: 1)));

        Assert.Equal(ProxyHttpStatus.BadGateway, exception.StatusCode);
        Assert.Contains("fallback-upstream", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, ocr.Calls.Count);
    }

    [Fact]
    public async Task SecondImage_SkipsRouteThatAlreadyFailedInSameRequest()
    {
        var ocr = new StubOcrService { FailingUpstreamModels = { "primary-upstream" } };
        var service = CreateService(ocr, TwoCandidates());

        var result = await service.RewriteAsync(CreateContext(imageCount: 2));

        Assert.True(result.UsedOcr);
        // 第一张图:主失败后转兜底(累计 2 次);第二张图:直接用兜底(累计第 3 次)。
        Assert.Equal(3, ocr.Calls.Count);
        Assert.Single(ocr.Calls, call => call.VisionRoute!.UpstreamModel == "primary-upstream");
        Assert.Equal(2, ocr.Calls[2].Image.ImageNumber);
        Assert.Equal(3, ocr.Calls[2].Attempt);
        Assert.Equal(ProxyVisionRouteKinds.Fallback, ocr.Calls[2].RouteKind);
    }

    [Fact]
    public async Task SecondImage_AttemptCountsAcrossImagesWithinRequest()
    {
        var ocr = new StubOcrService();
        var service = CreateService(ocr, TwoCandidates());

        await service.RewriteAsync(CreateContext(imageCount: 2));

        Assert.Equal(2, ocr.Calls.Count);
        Assert.Equal(1, ocr.Calls[0].Attempt);
        Assert.Equal(2, ocr.Calls[1].Attempt);
    }

    [Fact]
    public async Task MainRequestRetry_ReusesFailedRouteMemory()
    {
        var ocr = new StubOcrService { FailingUpstreamModels = { "primary-upstream" } };
        var service = CreateService(ocr, TwoCandidates());
        var context = CreateContext(imageCount: 1);

        // 同一个 context 代表同一次客户端请求,主请求换渠道重试会再次调用 RewriteAsync。
        await service.RewriteAsync(context);
        await service.RewriteAsync(context);

        Assert.Single(ocr.Calls, call => call.VisionRoute!.UpstreamModel == "primary-upstream");
        Assert.Equal(2, ocr.Calls.Count(call => call.VisionRoute!.UpstreamModel == "fallback-upstream"));
    }

    [Fact]
    public async Task MainRequestRetry_AttemptCountKeepsIncreasing()
    {
        var ocr = new StubOcrService();
        var service = CreateService(ocr, TwoCandidates());
        var context = CreateContext(imageCount: 1);

        await service.RewriteAsync(context);
        await service.RewriteAsync(context);

        Assert.Equal(2, ocr.Calls.Count);
        Assert.Equal(1, ocr.Calls[0].Attempt);
        Assert.Equal(2, ocr.Calls[1].Attempt);
    }

    [Fact]
    public async Task NoCandidates_AttemptCountIncrementsAcrossCalls()
    {
        var ocr = new StubOcrService();
        var routes = new VisionTransferRoutesDto(
            configured: true,
            [],
            VisionTransferUnavailableReasons.ChannelUnavailable);
        var service = CreateService(ocr, routes);
        var context = CreateContext(imageCount: 1);

        await service.RewriteAsync(context);
        await service.RewriteAsync(context);

        Assert.Equal(2, ocr.Calls.Count);
        Assert.Equal(1, ocr.Calls[0].Attempt);
        Assert.Equal(2, ocr.Calls[1].Attempt);
    }

    [Fact]
    public async Task NoCandidates_CallsOcrOnceWithReasonAndNoRoute()
    {
        var ocr = new StubOcrService();
        var routes = new VisionTransferRoutesDto(
            configured: true,
            [],
            VisionTransferUnavailableReasons.ChannelUnavailable);
        var service = CreateService(ocr, routes);

        await service.RewriteAsync(CreateContext(imageCount: 1));

        var call = Assert.Single(ocr.Calls);
        Assert.Null(call.VisionRoute);
        Assert.Equal(ProxyVisionRouteKinds.None, call.RouteKind);
        Assert.Equal(VisionTransferUnavailableReasons.ChannelUnavailable, call.UnavailableReason);
    }

    [Fact]
    public async Task ConfigurationErrorFromOcr_IsNotRetried()
    {
        var ocr = new StubOcrService { BadRequestUpstreamModels = { "primary-upstream" } };
        var service = CreateService(ocr, TwoCandidates());

        await Assert.ThrowsAsync<BadRequestException>(
            () => service.RewriteAsync(CreateContext(imageCount: 1)));

        Assert.Single(ocr.Calls);
    }

    private static ProxyImageFallbackService CreateService(
        StubOcrService ocr,
        VisionTransferRoutesDto routes)
    {
        return new ProxyImageFallbackService(new StubRewriter(), ocr, new StubRouteService(routes));
    }

    private static VisionTransferRoutesDto TwoCandidates()
    {
        return new VisionTransferRoutesDto(
            configured: true,
            [
                Route("primary-channel", "primary-model", "primary-upstream"),
                Route("fallback-channel", "fallback-model", "fallback-upstream")
            ],
            string.Empty);
    }

    private static ProxyRouteDto Route(string channelId, string model, string upstreamModel)
    {
        return new ProxyRouteDto(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = channelId,
                ["name"] = channelId,
                ["type"] = "chat"
            },
            model,
            upstreamModel,
            supportsImage: true,
            matchedModelMapping: true);
    }

    private static ProxyImageFallbackContext CreateContext(int imageCount)
    {
        return new ProxyImageFallbackContext(
            "request-1",
            "alice",
            null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["image_count"] = imageCount },
            "responses",
            "text-model",
            30,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            CancellationToken.None,
            new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// 按 payload 里的 image_count 生成对应数量的用户图片,不做真实的载荷改写。
    /// </summary>
    private sealed class StubRewriter : IProxyImagePayloadRewriter
    {
        public ProxyImagePayloadRewritePlan Prepare(
            Dictionary<string, object?> payload,
            string entryProtocol)
        {
            var imageCount = payload.TryGetValue("image_count", out var value) && value is int count ? count : 1;
            var images = Enumerable.Range(1, imageCount)
                .Select(number => new ProxyImageInput(
                    number,
                    ProxyImageSourceKinds.Url,
                    $"https://example.test/{number}.png",
                    null,
                    "image/png"))
                .ToList();
            return new ProxyImagePayloadRewritePlan(payload, images, []);
        }

        public Dictionary<string, object?> ApplyOcrResults(
            ProxyImagePayloadRewritePlan plan,
            IReadOnlyList<ProxyOcrResult> results)
        {
            return plan.Payload;
        }
    }

    private sealed class StubRouteService : IProxyRouteService
    {
        private readonly VisionTransferRoutesDto _routes;

        public StubRouteService(VisionTransferRoutesDto routes)
        {
            _routes = routes;
        }

        public Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(string ownerUsername, string? model)
            => Task.FromResult<IReadOnlyList<ProxyRouteDto>>([]);

        public Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string ownerUsername)
            => Task.FromResult(_routes);

        public Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername)
            => Task.FromResult<IReadOnlyList<ProxyModelCapabilityDto>>([]);
    }

    private sealed class StubOcrService : IProxyOcrService
    {
        public List<ProxyOcrContext> Calls { get; } = [];

        public HashSet<string> FailingUpstreamModels { get; } = new(StringComparer.Ordinal);

        public HashSet<string> BadRequestUpstreamModels { get; } = new(StringComparer.Ordinal);

        public Task<ProxyOcrResult> RecognizeAsync(ProxyOcrContext context)
        {
            Calls.Add(context);
            var upstreamModel = context.VisionRoute?.UpstreamModel ?? string.Empty;
            if (BadRequestUpstreamModels.Contains(upstreamModel))
            {
                throw new BadRequestException($"bad request for {upstreamModel}");
            }

            if (FailingUpstreamModels.Contains(upstreamModel))
            {
                throw new UpstreamException(
                    $"vision OCR failed for {upstreamModel}",
                    ProxyHttpStatus.BadGateway,
                    body: null);
            }

            return Task.FromResult(new ProxyOcrResult(
                context.Image.ImageNumber,
                "TEXT",
                "描述",
                ProxyOcrEngines.Vision,
                context.Image.SourceKind,
                cacheHit: false));
        }
    }
}
