namespace OpenCodex.Api.Errors;

public sealed class RoutingException : ProxyException
{
    public RoutingException(string message, int? statusCode = null)
        : base(message, statusCode ?? ProxyHttpStatus.BadRequest)
    {
    }

    public override string ErrorType { get; } = "routing_error";
}
