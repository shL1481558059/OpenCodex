using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 表示一次入站请求中可用于日志关联和上游会话路由的身份信息。
/// </summary>
public sealed class ProxyConversationIdentity
{
    private ProxyConversationIdentity(
        string? threadId,
        string? sessionId,
        string? promptCacheKey,
        string? clientConversationId,
        string? turnId,
        string? windowId,
        string? previousResponseId)
    {
        ConversationKey = threadId is not null
            ? $"thread:{threadId}"
            : sessionId is not null
                ? $"session:{sessionId}"
                : promptCacheKey is not null
                    ? $"prompt_cache_key:{promptCacheKey}"
                    : clientConversationId is not null
                        ? $"conversation:{clientConversationId}"
                        : null;
        UpstreamSessionId = threadId ?? sessionId ?? promptCacheKey ?? clientConversationId;
        TurnId = turnId;
        WindowId = windowId;
        PreviousResponseId = previousResponseId;
    }

    /// <summary>
    /// 获取用于日志检索的规范化会话键。
    /// </summary>
    public string? ConversationKey { get; }

    /// <summary>
    /// 获取用于上游会话路由的未加前缀标识。
    /// </summary>
    public string? UpstreamSessionId { get; }

    /// <summary>
    /// 获取当前轮次标识。
    /// </summary>
    public string? TurnId { get; }

    /// <summary>
    /// 获取当前窗口标识。
    /// </summary>
    public string? WindowId { get; }

    /// <summary>
    /// 获取当前请求引用的上一响应标识。
    /// </summary>
    public string? PreviousResponseId { get; }

    /// <summary>
    /// 从入站请求头和请求体提取会话身份。
    /// </summary>
    public static ProxyConversationIdentity From(
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, object?>? payload)
    {
        var turnMetadata = ParseTurnMetadata(HeaderValue(requestHeaders, "x-codex-turn-metadata"));
        var threadId = MetadataValue(turnMetadata, "thread_id")
            ?? HeaderValue(requestHeaders, "thread-id");
        var sessionId = MetadataValue(turnMetadata, "session_id")
            ?? HeaderValue(requestHeaders, "session-id")
            ?? HeaderValue(requestHeaders, "x-claude-code-session-id");
        var promptCacheKey = payload is null
            ? null
            : NullIfEmpty(JsonDictionaryValue.String(payload, "prompt_cache_key"));
        var clientConversationId = HeaderValue(requestHeaders, "x-conversation-id");
        var turnId = MetadataValue(turnMetadata, "turn_id")
            ?? HeaderValue(requestHeaders, "x-client-request-id");
        var windowId = MetadataValue(turnMetadata, "window_id")
            ?? HeaderValue(requestHeaders, "x-codex-window-id");
        var previousResponseId = payload is null
            ? null
            : NullIfEmpty(JsonDictionaryValue.String(payload, "previous_response_id"));

        return new ProxyConversationIdentity(
            threadId,
            sessionId,
            promptCacheKey,
            clientConversationId,
            turnId,
            windowId,
            previousResponseId);
    }

    private static Dictionary<string, string> ParseTurnMetadata(string? value)
    {
        if (value is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? MetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return metadata.TryGetValue(key, out var value) ? NullIfEmpty(value) : null;
    }

    private static string? HeaderValue(
        IReadOnlyDictionary<string, string> headers,
        string key)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return NullIfEmpty(pair.Value);
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
