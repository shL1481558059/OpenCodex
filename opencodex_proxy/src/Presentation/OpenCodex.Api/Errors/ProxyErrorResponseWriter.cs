using System.Text.Json;
using OpenCodex.Api.Infrastructure;
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
    /// <remarks>
    /// <see cref="HttpResponse.Clear"/> 会清空已设置的响应头，因此与请求体未消费相关的
    /// <c>Connection: close</c> 必须在其之后设置。
    /// </remarks>
    public static async Task WriteAsync(HttpContext context, ProxyException exception)
    {
        // UpstreamException 携带上游原始状态码，返回给客户端时统一使用 502，
        // 避免暴露上游/渠道内部信息。
        var clientStatusCode = exception is UpstreamException
            ? ProxyHttpStatus.BadGateway
            : exception.StatusCode;

        var response = IsProxyCompatibilityEndpoint(context)
            ? exception.ToResponse()
            : ApiOpResult.Fail(
                clientStatusCode,
                exception is UpstreamException ? UpstreamClientMessage : exception.Message);

        await WriteAsync(context, clientStatusCode, response);
    }

    /// <summary>
    /// 写出未处理异常对应的 500 响应。
    /// </summary>
    /// <param name="context">当前请求上下文。</param>
    public static async Task WriteInternalErrorAsync(HttpContext context)
    {
        await WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            ApiOpResult.Fail(StatusCodes.Status500InternalServerError, "An unexpected error occurred."));
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, object response)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        // 请求体未读到 EOF 时连接已无法再承载下一个请求：Kestrel 会在响应收尾时中止它，
        // 而客户端可能已经把这条 keep-alive 连接放回池子。显式关闭可让客户端立刻换新连接，
        // 不会拿到裸连接重置。必须在 Clear() 之后设置。
        if (context.Items.ContainsKey(RequestBodyReader.OversizedBodyNotDrainedItemKey))
        {
            context.Response.Headers.Connection = "close";
        }

        // 超限请求的取消令牌可能已被触发，这段小错误响应仍必须完整写出，
        // 否则客户端看到的是裸重置而不是 413。
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(response, JsonOptions),
            CancellationToken.None);
    }
}
