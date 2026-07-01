using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Errors;

public sealed class ProxyErrorMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ProxyErrorMiddleware> _logger;

    public ProxyErrorMiddleware(
        RequestDelegate next,
        ILogger<ProxyErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ProxyException exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            // UpstreamException 携带上游原始状态码，返回给客户端时统一使用 502，
            // 避免暴露上游/渠道内部信息。
            var clientStatusCode = exception is UpstreamException
                ? ProxyHttpStatus.BadGateway
                : exception.StatusCode;

            context.Response.Clear();
            context.Response.StatusCode = clientStatusCode;
            context.Response.ContentType = "application/json";

            var response = IsProxyCompatibilityEndpoint(context)
                ? exception.ToResponse()
                : ApiOpResult.Fail(
                    clientStatusCode,
                    exception is UpstreamException
                        ? "An upstream error occurred. Please try again later."
                        : exception.Message);

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response, JsonOptions),
                context.RequestAborted);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            _logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var response = ApiOpResult.Fail(
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.");

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response, JsonOptions),
                context.RequestAborted);
        }
    }

    private static bool IsProxyCompatibilityEndpoint(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal);
    }

}
