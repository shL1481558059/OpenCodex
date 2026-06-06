using System.Text.Json;
using OpenCodex.Api.DTOs.Results;

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

            context.Response.Clear();
            context.Response.StatusCode = exception.StatusCode;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(exception.ToResponse(), JsonOptions),
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
                "Unhandled exception while processing request {TraceId}",
                context.TraceIdentifier);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var response = ApiResult.Fail(
                500000,
                "An unexpected error occurred.",
                traceId: context.TraceIdentifier);

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response, JsonOptions),
                context.RequestAborted);
        }
    }
}
