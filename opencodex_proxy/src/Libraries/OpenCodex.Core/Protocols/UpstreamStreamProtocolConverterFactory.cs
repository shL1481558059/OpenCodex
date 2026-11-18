namespace OpenCodex.Core.Protocols;

internal sealed class UpstreamStreamProtocolConverterFactory
    : IUpstreamStreamProtocolConverterFactory
{
    public static UpstreamStreamProtocolConverterFactory Default { get; } = new();

    private readonly IReadOnlyDictionary<
        (string UpstreamProtocol, string ClientProtocol),
        IUpstreamStreamProtocolConverter> _converters;

    private UpstreamStreamProtocolConverterFactory()
    {
        IUpstreamStreamProtocolConverter[] converters =
        [
            new ChatToResponsesStreamConverter(),
            new MessagesToResponsesStreamConverter(),
            new ChatToMessagesStreamConverter(),
            new MessagesToChatStreamConverter(),
            new ResponsesToChatStreamConverter(),
            new ResponsesToMessagesStreamConverter()
        ];

        _converters = converters.ToDictionary(
            converter => (converter.UpstreamProtocol, converter.ClientProtocol));
    }

    public bool Supports(string upstreamProtocol, string clientProtocol)
    {
        return upstreamProtocol == clientProtocol
            || _converters.ContainsKey((upstreamProtocol, clientProtocol));
    }

    public IUpstreamStreamProtocolConverter Resolve(
        string upstreamProtocol,
        string clientProtocol)
    {
        if (_converters.TryGetValue((upstreamProtocol, clientProtocol), out var converter))
        {
            return converter;
        }

        throw new InvalidOperationException(
            $"streaming conversion not implemented for {upstreamProtocol} to {clientProtocol}");
    }
}
