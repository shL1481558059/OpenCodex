using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Abstractions;

public interface IImagesUpstreamClient
{
    Task<ImageUpstreamResponse> GenerateAsync(
        IReadOnlyDictionary<string, object?> channel,
        ImageGenerationRequest request,
        int defaultTimeout,
        CancellationToken cancellationToken);

    Task<ImageUpstreamResponse> EditAsync(
        IReadOnlyDictionary<string, object?> channel,
        ImageEditRequest request,
        int defaultTimeout,
        CancellationToken cancellationToken);
}

public sealed class ImageUpstreamResponse : IAsyncDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    public ImageUpstreamResponse(
        int statusCode,
        string? contentType,
        Stream content,
        IReadOnlyDictionary<string, string>? headers = null,
        IDisposable? owner = null)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Headers = headers ?? new Dictionary<string, string>();
        _owner = owner;
    }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public Stream Content { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Content.DisposeAsync();
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}
