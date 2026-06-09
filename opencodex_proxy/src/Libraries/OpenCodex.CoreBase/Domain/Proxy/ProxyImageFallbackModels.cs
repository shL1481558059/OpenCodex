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

    public const string PaddleOcr = "paddleocr";
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
    long? apiKeyId,
    Dictionary<string, object?> payload,
    string entryProtocol,
    string? requestModel,
    int defaultTimeout,
    ProxyRequestMetadata requestMetadata,
    CancellationToken cancellationToken)
{
    public string RequestId { get; } = requestId;

    public string OwnerUsername { get; } = ownerUsername;

    public long? ApiKeyId { get; } = apiKeyId;

    public Dictionary<string, object?> Payload { get; } = payload;

    public string EntryProtocol { get; } = entryProtocol;

    public string? RequestModel { get; } = requestModel;

    public int DefaultTimeout { get; } = defaultTimeout;

    public ProxyRequestMetadata RequestMetadata { get; } = requestMetadata;

    public CancellationToken CancellationToken { get; } = cancellationToken;
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
    long? apiKeyId,
    ProxyRequestMetadata requestMetadata,
    ProxyImageInput image,
    ProxyRouteDto? visionRoute,
    int defaultTimeout,
    CancellationToken cancellationToken)
{
    public string RequestId { get; } = requestId;

    public string OwnerUsername { get; } = ownerUsername;

    public long? ApiKeyId { get; } = apiKeyId;

    public ProxyRequestMetadata RequestMetadata { get; } = requestMetadata;

    public ProxyImageInput Image { get; } = image;

    public ProxyRouteDto? VisionRoute { get; } = visionRoute;

    public int DefaultTimeout { get; } = defaultTimeout;

    public CancellationToken CancellationToken { get; } = cancellationToken;
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
