namespace OpenCodex.Core.Protocols;

internal abstract class UpstreamStreamProtocolConverter : IUpstreamStreamProtocolConverter
{
    protected UpstreamStreamProtocolConverter(
        string upstreamProtocol,
        string clientProtocol)
    {
        UpstreamProtocol = upstreamProtocol;
        ClientProtocol = clientProtocol;
    }

    public string UpstreamProtocol { get; }
    public string ClientProtocol { get; }

    public abstract IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken);
}

internal sealed class ChatToResponsesStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Chat, ProtocolConverter.Responses)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.ChatToResponsesFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            context.SkipToolNames,
            context.SkipResponseCreated,
            context.InitialSequenceNumber,
            context.InitialOutputIndex,
            cancellationToken);
    }
}

internal sealed class MessagesToResponsesStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Messages, ProtocolConverter.Responses)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.MessagesToResponsesFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            context.SkipToolNames,
            context.SkipResponseCreated,
            context.InitialSequenceNumber,
            context.InitialOutputIndex,
            cancellationToken);
    }
}

internal sealed class ChatToMessagesStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Chat, ProtocolConverter.Messages)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.ChatToMessagesFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            null,
            false,
            cancellationToken);
    }
}

internal sealed class MessagesToChatStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Messages, ProtocolConverter.Chat)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.MessagesToChatFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            null,
            context.IncludeUsage,
            cancellationToken);
    }
}

internal sealed class ResponsesToChatStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Responses, ProtocolConverter.Chat)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.ResponsesToChatFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            null,
            cancellationToken);
    }
}

internal sealed class ResponsesToMessagesStreamConverter()
    : UpstreamStreamProtocolConverter(ProtocolConverter.Responses, ProtocolConverter.Messages)
{
    public override IAsyncEnumerable<SseFrame> ConvertAsync(
        UpstreamStreamConversionContext context,
        CancellationToken cancellationToken)
    {
        return SseStreamConverter.ResponsesToMessagesFrames(
            context.UpstreamEvents,
            context.Model,
            context.Result,
            null,
            false,
            cancellationToken);
    }
}
