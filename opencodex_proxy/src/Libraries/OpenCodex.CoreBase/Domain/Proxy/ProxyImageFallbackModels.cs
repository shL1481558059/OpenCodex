using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.CoreBase.Domain.Proxy;

public static class ProxyImageSourceKinds
{
    public const string Data = "data";

    public const string Url = "url";
}

public static class ProxyOcrEngines
{
    public const string Vision = "vision";
}

/// <summary>
/// 定义视觉转移路由在一次 OCR 尝试中的角色标识,写入 ocr_details.route_kind。
/// </summary>
public static class ProxyVisionRouteKinds
{
    /// <summary>配置的主视觉路由。</summary>
    public const string Primary = "primary";

    /// <summary>配置的兜底视觉路由。</summary>
    public const string Fallback = "fallback";

    /// <summary>没有可用候选,本次尝试只用于留下失败日志。</summary>
    public const string None = "none";
}

public sealed class ProxyImageInput(
    int imageNumber,
    string sourceKind,
    string imageReference,
    byte[]? imageBytes,
    string mediaType)
{
    public int ImageNumber { get; } = imageNumber;

    public string SourceKind { get; } = sourceKind;

    public string ImageReference { get; } = imageReference;

    public byte[]? ImageBytes { get; } = imageBytes;

    public string MediaType { get; } = mediaType;
}

public sealed class ProxyImageInjectionTarget(
    int imageNumber,
    List<object?> contentBlocks,
    string textBlockType)
{
    public int ImageNumber { get; } = imageNumber;

    public List<object?> ContentBlocks { get; } = contentBlocks;

    public string TextBlockType { get; } = textBlockType;
}

public sealed class ProxyImagePayloadRewritePlan(
    Dictionary<string, object?> payload,
    IReadOnlyList<ProxyImageInput> userImages,
    IReadOnlyList<ProxyImageInjectionTarget> injectionTargets)
{
    public Dictionary<string, object?> Payload { get; } = payload;

    public IReadOnlyList<ProxyImageInput> UserImages { get; } = userImages;

    public IReadOnlyList<ProxyImageInjectionTarget> InjectionTargets { get; } = injectionTargets;
}

public sealed class ProxyImageFallbackContext(
    string requestId,
    string ownerUsername,
    Guid? apiKeyId,
    Dictionary<string, object?> payload,
    string entryProtocol,
    string? requestModel,
    int defaultTimeout,
    ProxyRequestMetadata requestMetadata,
    CancellationToken cancellationToken,
    ISet<string> failedVisionRoutes)
{
    public string RequestId { get; } = requestId;

    public string OwnerUsername { get; } = ownerUsername;

    public Guid? ApiKeyId { get; } = apiKeyId;

    public Dictionary<string, object?> Payload { get; } = payload;

    public string EntryProtocol { get; } = entryProtocol;

    public string? RequestModel { get; } = requestModel;

    public int DefaultTimeout { get; } = defaultTimeout;

    public ProxyRequestMetadata RequestMetadata { get; } = requestMetadata;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// 获取本请求内已发起的 OCR 尝试总数。主请求换渠道重试时共享同一上下文,
    /// 累计计数保持递增,保证日志里的 attempt 不会在重试后重置。
    /// </summary>
    public int RequestAttemptCount { get; set; }

    /// <summary>
    /// 获取请求级的失败视觉路由集合。主请求换渠道重试时会再次进入本流程,
    /// 共享这个集合可以避免对同一个已失败的视觉路由反复发起子请求。
    /// </summary>
    public ISet<string> FailedVisionRoutes { get; } = failedVisionRoutes;
}

public sealed class ProxyImageFallbackResult(
    Dictionary<string, object?> payload,
    bool usedOcr)
{
    public Dictionary<string, object?> Payload { get; } = payload;

    public bool UsedOcr { get; } = usedOcr;
}

public sealed class ProxyOcrContext(
    string requestId,
    string ownerUsername,
    Guid? apiKeyId,
    ProxyRequestMetadata requestMetadata,
    ProxyImageInput image,
    ProxyRouteDto? visionRoute,
    int defaultTimeout,
    CancellationToken cancellationToken,
    string routeKind,
    int attempt,
    string unavailableReason)
{
    public string RequestId { get; } = requestId;

    public string OwnerUsername { get; } = ownerUsername;

    public Guid? ApiKeyId { get; } = apiKeyId;

    public ProxyRequestMetadata RequestMetadata { get; } = requestMetadata;

    public ProxyImageInput Image { get; } = image;

    public ProxyRouteDto? VisionRoute { get; } = visionRoute;

    public int DefaultTimeout { get; } = defaultTimeout;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// 获取本次尝试使用的路由角色,取值见 <see cref="ProxyVisionRouteKinds"/>。
    /// </summary>
    public string RouteKind { get; } = routeKind;

    /// <summary>
    /// 获取本张图片的第几次尝试,从 1 开始。
    /// </summary>
    public int Attempt { get; } = attempt;

    /// <summary>
    /// 获取没有可用候选时的原因标识;有候选时为空字符串。
    /// </summary>
    public string UnavailableReason { get; } = unavailableReason;
}

public sealed class ProxyOcrResult(
    int imageNumber,
    string text,
    string description,
    string engine,
    string sourceKind,
    bool cacheHit)
{
    public int ImageNumber { get; } = imageNumber;

    public string Text { get; } = text;

    public string Description { get; } = description;

    public string Engine { get; } = engine;

    public string SourceKind { get; } = sourceKind;

    public bool CacheHit { get; } = cacheHit;
}
