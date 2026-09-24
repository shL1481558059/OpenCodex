using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Errors;

/// <summary>
/// 把代理异常写成对外响应，供错误中间件与认证挑战共用。
/// </summary>
internal static class ProxyErrorResponseWriter
{
    private const string UpstreamClientMessage = "An upstream error occurred. Please try again later.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 判断请求是否属于 OpenAI 兼容路径，兼容路径使用协议原生错误结构。
    /// </summary>
    /// <param name="context">当前请求上下文。</param>
    /// <returns>属于兼容路径时为 <see langword="true"/>。</returns>
    public static bool IsProxyCompatibilityEndpoint(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal);
    }

    /// <summary>
    /// 写出代理异常对应的状态码与响应体。
    /// </summary>
    /// <param name="context">当前请求上下文。</param>
    /// <param name="exception">待写出的代理异常。</param>
    public static async Task WriteAsync(HttpContext context, ProxyException exception)
    {
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
                exception is UpstreamException ? UpstreamClientMessage : exception.Message);

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(response, JsonOptions),
            context.RequestAborted);
    }

    /// <summary>
    /// 写出未处理异常对应的 500 响应。
    /// </summary>
    /// <param name="context">当前请求上下文。</param>
    public static async Task WriteInternalErrorAsync(HttpContext context)
    {
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
