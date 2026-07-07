using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    // Anthropic 等上游在并发超限或过载时，会用 HTTP 200 + SSE body 返回 error 事件。
    // 这些 error.type 需要当作可重试错误处理，而不是透传给客户端。
    private static readonly HashSet<string> RetryableStreamErrorTypes =
        new(StringComparer.Ordinal) { "rate_limit_error", "overloaded_error" };

    private static (string ErrorType, string Message)? TryGetRetryableErrorFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var type = typeEl.GetString();
        if (!string.Equals(type, "error", StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("error", out var errorEl) || errorEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!errorEl.TryGetProperty("type", out var errorTypeEl) || errorTypeEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var errorType = errorTypeEl.GetString();
        if (errorType is null || !RetryableStreamErrorTypes.Contains(errorType))
        {
            return null;
        }

        var message = errorEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString() ?? errorType
            : errorType;

        return (errorType, message);
    }

    private static async Task<Dictionary<string, object?>> ReadJsonObject(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (TryGetRetryableErrorFromElement(root) is { } retryable)
            {
                throw new UpstreamException(
                    retryable.Message,
                    ProxyHttpStatus.TooManyRequests,
                    body: FromJsonElement(root),
                    channelId: JsonDictionaryValue.String(channel, "id"));
            }
            var value = FromJsonElement(root);
            if (value is Dictionary<string, object?> dictionary)
            {
                return dictionary;
            }
        }
        catch (JsonException)
        {
            throw new UpstreamException(
                "upstream returned invalid JSON",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }

        throw new UpstreamException(
            "upstream returned invalid JSON",
            ProxyHttpStatus.BadGateway,
            channelId: JsonDictionaryValue.String(channel, "id"));
    }

    private static async Task<Dictionary<string, object?>> ReadJsonModelList(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var value = FromJsonElement(document.RootElement);
            return value switch
            {
                Dictionary<string, object?> dictionary => dictionary,
                List<object?> list => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["data"] = list
                },
                _ => throw new UpstreamException(
                    "upstream returned invalid JSON",
                    ProxyHttpStatus.BadGateway,
                    channelId: JsonDictionaryValue.String(channel, "id"))
            };
        }
        catch (JsonException)
        {
            throw new UpstreamException(
                "upstream returned invalid JSON",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }
    }

    private static async Task ThrowHttpError(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new UpstreamException(
            $"upstream returned HTTP {(int)response.StatusCode}",
            (int)response.StatusCode,
            DecodeBody(bodyText),
            JsonDictionaryValue.String(channel, "id"));
    }

    private static object? DecodeBody(string bodyText)
    {
        if (bodyText.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyText);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return bodyText.Length <= 2000 ? bodyText : bodyText[..2000];
        }
    }

    private static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
