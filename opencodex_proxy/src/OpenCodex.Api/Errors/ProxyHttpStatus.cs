namespace OpenCodex.Api.Errors;

public static class ProxyHttpStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int InternalServerError = 500;
    public const int BadGateway = 502;
    public const int GatewayTimeout = 504;
}
