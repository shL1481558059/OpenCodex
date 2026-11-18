namespace OpenCodex.Core.Protocols;

internal sealed class UpstreamStreamConversionContext
{
    public IAsyncEnumerable<SseEvent> UpstreamEvents { get; init; } = AsyncEnumerable.Empty<SseEvent>();
    public string? Model { get; init; }
    public required ConvertedStreamResult Result { get; init; }
    public IReadOnlySet<string>? SkipToolNames { get; init; }
    public bool IncludeUsage { get; init; }
    public bool SkipResponseCreated { get; init; }
    public int InitialSequenceNumber { get; init; }
    public int InitialOutputIndex { get; init; }
}

public static partial class SseStreamConverter
{
    internal static bool SupportsUpstreamStreamConversion(
        string clientProtocol,
        string upstreamProtocol)
    {
        return UpstreamStreamProtocolConverterFactory.Default.Supports(
            upstreamProtocol,
            clientProtocol);
    }

    internal static IAsyncEnumerable<SseFrame> ConvertUpstreamStreamToClient(
        UpstreamStreamConversionContext context,
        string upstreamProtocol,
        string clientProtocol,
        CancellationToken cancellationToken)
    {
        return UpstreamStreamProtocolConverterFactory.Default
            .Resolve(upstreamProtocol, clientProtocol)
            .ConvertAsync(context, cancellationToken);
    }
}
