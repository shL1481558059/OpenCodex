namespace OpenCodex.Api.Infrastructure;

public sealed class RequestTraceMiddleware
{
    public const string HeaderName = "X-Request-Id";

    private readonly RequestDelegate _next;

    public RequestTraceMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var incomingTraceId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(incomingTraceId))
        {
            context.TraceIdentifier = incomingTraceId.Trim();
        }

        context.Response.OnStarting(() =>
        {
            SetResponseHeader(context);
            return Task.CompletedTask;
        });

        await _next(context);

        if (!context.Response.HasStarted)
        {
            SetResponseHeader(context);
        }
    }

    private static void SetResponseHeader(HttpContext context)
    {
        context.Response.Headers[HeaderName] = context.TraceIdentifier;
    }
}
