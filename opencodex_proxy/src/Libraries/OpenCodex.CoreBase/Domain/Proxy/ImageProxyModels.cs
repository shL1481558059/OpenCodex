using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class ImageProxyParameters
{
    public ImageProxyParameters(IReadOnlyDictionary<string, object?> payload)
    {
        Payload = new Dictionary<string, object?>(payload, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, object?> Payload { get; }

    public string? Model => StringValue("model");

    public string? Prompt => StringValue("prompt");

    public ImageProxyParameters WithModel(string model)
    {
        var payload = new Dictionary<string, object?>(Payload, StringComparer.Ordinal)
        {
            ["model"] = model
        };
        return new ImageProxyParameters(payload);
    }

    private string? StringValue(string key)
        => Payload.TryGetValue(key, out var value) ? value as string : null;
}

public sealed class ImageProxyFileSource
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _openReadAsync;

    public ImageProxyFileSource(
        string fieldName,
        string fileName,
        string? contentType,
        long? length,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync)
    {
        FieldName = fieldName;
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        _openReadAsync = openReadAsync ?? throw new ArgumentNullException(nameof(openReadAsync));
    }

    public string FieldName { get; }

    public string FileName { get; }

    public string? ContentType { get; }

    public long? Length { get; }

    /// <summary>每次调用都必须返回位于起始位置、由调用方释放的新流。</summary>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => _openReadAsync(cancellationToken);
}

public sealed record ImageGenerationRequest(ImageProxyParameters Parameters);

public sealed record ImageEditRequest(
    ImageProxyParameters Parameters,
    IReadOnlyList<ImageProxyFileSource> Images,
    ImageProxyFileSource? Mask = null);

public sealed record ImageGenerationContext(
    ImageGenerationRequest Request,
    string? AuthorizationHeader,
    ProxyRequestMetadata RequestMetadata,
    IProxyResponseBodyWriter ResponseWriter,
    CancellationToken CancellationToken);

public sealed record ImageEditContext(
    ImageEditRequest Request,
    string? AuthorizationHeader,
    ProxyRequestMetadata RequestMetadata,
    IProxyResponseBodyWriter ResponseWriter,
    CancellationToken CancellationToken);
