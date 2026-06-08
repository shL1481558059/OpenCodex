namespace OpenCodex.Core.Errors;

public sealed class BadRequestException : ProxyException
{
    public BadRequestException(string message, int? statusCode = null)
        : base(message, statusCode ?? ProxyHttpStatus.BadRequest)
    {
    }

    public override string ErrorType { get; } = "bad_request";
}
