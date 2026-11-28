using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OpenCodex.Api.Errors;
using OpenCodex.Core.Errors;

namespace OpenCodex.Api.Infrastructure;

/// <summary>
/// 限制代理端点请求体大小，超限时返回协议兼容的 413 响应。
/// </summary>
/// <remarks>
/// 授权过滤器先于资源过滤器执行，因此未认证的超限请求仍返回 401；
/// 已认证请求在进入动作与读取请求体之前被拦截。
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProxyRequestBodyLimitAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var limit = ProxyRequestLimits.ResolveMaxRequestBodyBytes(
            httpContext.RequestServices.GetRequiredService<IConfiguration>());

        // Root MaxRequestBodyBytes 是解码后的负载预算；chunked 的传输分帧不属于负载，
        // 不能让它先于统一预算拦截，因此这里只对非 chunked 请求沿用 Kestrel 的线上上限。
        var chunked = httpContext.Request.Headers.TransferEncoding.ToString()
            .Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var sizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = chunked ? null : limit;
        }

        // 声明长度即可判定超限时仍然要把这段 body 消费掉：写出 413 后 Kestrel 会因未消费的
        // 请求体中止连接，而客户端此时已把这条 keep-alive 连接放回池子，下一个请求必然失败。
        if (httpContext.Request.ContentLength is { } contentLength && contentLength > limit)
        {
            await RejectAsync(httpContext, limit, 0);
            context.Result = new EmptyResult();
            return;
        }

        try
        {
            await next();
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // 响应已开始写出时不能再改写成 413，交给外层按原有语义处理。
            if (httpContext.Response.HasStarted)
            {
                throw;
            }

            // chunked 请求体没有 Content-Length，读取器在流式读取时才发现超限。
            await RejectAsync(httpContext, limit, limit);
            context.Result = new EmptyResult();
        }
    }

    private static async Task RejectAsync(HttpContext httpContext, long limit, long alreadyRead)
    {
        // 先放行 Kestrel 的线上上限，否则 drain 会在中途被自身的 wire 上限中止。
        var sizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        // 未读到 EOF 时必须关闭连接；读到 EOF 时连接可继续复用。
        if (!await RequestBodyReader.DrainOversizedBodyAsync(
                httpContext.Request,
                limit,
                alreadyRead,
                httpContext.RequestAborted))
        {
            httpContext.Items[RequestBodyReader.OversizedBodyNotDrainedItemKey] = true;
        }

        await ProxyErrorResponseWriter.WriteAsync(
            httpContext,
            new ProxyException("request body too large", StatusCodes.Status413PayloadTooLarge));
    }
}
