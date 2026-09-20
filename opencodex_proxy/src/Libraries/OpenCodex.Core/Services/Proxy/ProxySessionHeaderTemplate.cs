using System.Security.Cryptography;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 将渠道请求头中的会话占位符替换为当前请求的会话标识。
/// </summary>
public static class ProxySessionHeaderTemplate
{
    /// <summary>
    /// 渠道请求头支持的会话标识占位符。
    /// </summary>
    public const string SessionIdPlaceholder = "{{session_id}}";

    private const int MaxSessionIdLength = 256;

    /// <summary>
    /// 解析当前请求的上游会话标识，缺失或非法时生成随机标识。
    /// </summary>
    public static string ResolveSessionId(
        ProxyRequestMetadata requestMetadata,
        IReadOnlyDictionary<string, object?>? payload)
    {
        var identity = ProxyConversationIdentity.From(requestMetadata.Headers, payload);
        return IsValidSessionId(identity.UpstreamSessionId)
            ? identity.UpstreamSessionId!
            : CreateSessionId();
    }

    /// <summary>
    /// 生成符合 OpenCode 会话标识形态的随机值。
    /// </summary>
    public static string CreateSessionId()
    {
        return $"ses_{RandomNumberGenerator.GetHexString(16).ToLowerInvariant()}"
            + RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
    }

    /// <summary>
    /// 对路由渠道中的请求头执行会话占位符替换。
    /// </summary>
    public static ProxyRouteDto Apply(ProxyRouteDto route, string sessionId)
    {
        if (!ContainsPlaceholder(route.Channel))
        {
            return route;
        }

        return new ProxyRouteDto(
            ApplyToChannel(route.Channel, sessionId),
            route.OriginalModel,
            route.UpstreamModel,
            route.SupportsImage,
            route.MatchedModelMapping);
    }

    /// <summary>
    /// 对渠道配置中的请求头执行会话占位符替换。
    /// </summary>
    public static Dictionary<string, object?> ApplyToChannel(
        IReadOnlyDictionary<string, object?> channel,
        string sessionId)
    {
        var copied = WebSearchPayload.DeepCopyObject(channel);
        var headers = WebSearchPayload.TryAsObject(
            JsonDictionaryValue.Get(copied, "headers"),
            out var existingHeaders)
            ? existingHeaders
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var key in headers.Keys.ToList())
        {
            if (headers[key] is string text
                && text.Contains(SessionIdPlaceholder, StringComparison.Ordinal))
            {
                headers[key] = text.Replace(
                    SessionIdPlaceholder,
                    sessionId,
                    StringComparison.Ordinal);
            }
        }

        copied["headers"] = headers;
        return copied;
    }

    private static bool ContainsPlaceholder(IReadOnlyDictionary<string, object?> channel)
    {
        if (!WebSearchPayload.TryAsObject(
            JsonDictionaryValue.Get(channel, "headers"),
            out var headers))
        {
            return false;
        }

        return headers.Values.Any(value =>
            value is string text
            && text.Contains(SessionIdPlaceholder, StringComparison.Ordinal));
    }

    private static bool IsValidSessionId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSessionIdLength)
        {
            return false;
        }

        return value.All(character =>
            !char.IsControl(character)
            && character != '\u007f');
    }
}
