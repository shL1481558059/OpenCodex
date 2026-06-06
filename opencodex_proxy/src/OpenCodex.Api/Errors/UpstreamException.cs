namespace OpenCodex.Api.Errors;

public sealed class UpstreamException : ProxyException
{
    public UpstreamException(
        string message,
        int statusCode = ProxyHttpStatus.BadGateway,
        object? body = null,
        string? channelId = null)
        : base(message, statusCode)
    {
        Body = body;
        ChannelId = channelId;
    }

    public object? Body { get; }

    public string? ChannelId { get; }

    public override string ErrorType { get; } = "upstream_error";

    public override object ToResponse()
    {
        var error = new Dictionary<string, object?>
        {
            ["message"] = Message,
            ["type"] = ErrorType
        };

        if (!string.IsNullOrEmpty(ChannelId))
        {
            error["channel_id"] = ChannelId;
        }

        if (Body is not null)
        {
            error["upstream"] = Body;
        }

        return new
        {
            error
        };
    }
}
