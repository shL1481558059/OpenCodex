namespace OpenCodex.Core.Errors;

public class ProxyException : Exception
{
    public ProxyException(string message, int statusCode = ProxyHttpStatus.InternalServerError, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public virtual string ErrorType { get; } = "proxy_error";

    public virtual object ToResponse()
    {
        return new
        {
            error = new Dictionary<string, object?>
            {
                ["message"] = Message,
                ["type"] = ErrorType
            }
        };
    }
}
