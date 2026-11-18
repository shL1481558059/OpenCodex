namespace OpenCodex.Core.Protocols;

internal interface IUpstreamStreamProtocolConverter
{
    string UpstreamProtocol { get; }
    string ClientProtocol { get; }

    IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken);
}

internal interface IUpstreamStreamProtocolConverterFactory
{
    bool Supports(
        string upstreamProtocol,
        string clientProtocol);

    IUpstreamStreamProtocolConverter Resolve(
        string upstreamProtocol,
        string clientProtocol);
}
