namespace OpenCodex.Core.Errors;

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
        return new
        {
            error = new Dictionary<string, object?>
            {
                ["message"] = "An upstream error occurred. Please try again later.",
                ["type"] = ErrorType
            }
        };
    }
}
