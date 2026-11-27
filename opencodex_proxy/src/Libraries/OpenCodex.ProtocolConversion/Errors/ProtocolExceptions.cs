namespace OpenCodex.ProtocolConversion.Errors;

public sealed class BadRequestException(
    string message,
    int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorType => "bad_request";
}

public sealed class UpstreamException(
    string message,
    string? errorCode = null,
    int statusCode = 502) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorType => "upstream_error";
    public string? ErrorCode { get; } = errorCode;
}
