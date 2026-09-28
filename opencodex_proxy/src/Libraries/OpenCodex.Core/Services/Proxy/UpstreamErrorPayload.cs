using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 构造日志用的上游错误载荷。代理与渠道诊断共用同一实现，
/// 避免两条路径各自维护合并规则而再次分叉。
/// </summary>
internal static class UpstreamErrorPayload
{
    public static Dictionary<string, object?>? FromException(ProxyException exception)
    {
        if (exception is UpstreamException { Body: not null } upstream)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = upstream.Body
            };
        }

        return null;
    }

    /// <summary>
    /// 合并已捕获的响应与上游错误体。捕获结果只剩 <c>_opencodex_capture</c> 元数据时，
    /// 以错误体为准并把元数据挂回去，保证原始上游错误不被丢弃。
    /// </summary>
    public static Dictionary<string, object?>? Combine(
        Dictionary<string, object?>? captured,
        Dictionary<string, object?>? errorResponse)
    {
        if (captured is null)
        {
            return errorResponse;
        }

        if (errorResponse is null)
        {
            return captured;
        }

        var hasProtocolResponse = captured.Keys.Any(key => key != "_opencodex_capture");
        if (!hasProtocolResponse)
        {
            if (captured.TryGetValue("_opencodex_capture", out var captureMetadata))
            {
                errorResponse["_opencodex_capture"] = captureMetadata;
            }

            return errorResponse;
        }

        foreach (var (key, value) in errorResponse)
        {
            captured.TryAdd(key, value);
        }

        return captured;
    }
}
