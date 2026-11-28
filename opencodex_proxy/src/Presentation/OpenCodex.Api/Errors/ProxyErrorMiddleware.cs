using OpenCodex.Core.Errors;

namespace OpenCodex.Api.Errors;

public sealed class ProxyErrorMiddleware
{
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

            await ProxyErrorResponseWriter.WriteAsync(context, exception);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            // chunked 请求体没有 Content-Length，Kestrel 在读取过程中才发现超限；
            // 是否关闭连接由 RequestBodyReader 的 drain 结果决定，写入器统一处理。
            await ProxyErrorResponseWriter.WriteAsync(
                context,
                new ProxyException("request body too large", StatusCodes.Status413PayloadTooLarge));
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

            await ProxyErrorResponseWriter.WriteInternalErrorAsync(context);
        }
    }
}
