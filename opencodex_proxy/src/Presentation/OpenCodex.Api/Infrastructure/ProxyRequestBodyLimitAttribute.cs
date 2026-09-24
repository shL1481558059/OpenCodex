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

        var sizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        if (httpContext.Request.ContentLength is { } contentLength && contentLength > limit)
        {
            await ProxyErrorResponseWriter.WriteAsync(
                httpContext,
                new ProxyException("request body too large", StatusCodes.Status413PayloadTooLarge));
            context.Result = new EmptyResult();
            return;
        }

        await next();
    }
}
