using System.Text.Json;
using Mapster;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services.Mapping;

public static class OpenCodexMappingConfig
{
    public static void Register()
    {
        TypeAdapterConfig<User, UserDto>
            .NewConfig()
            .MapWith(source => new UserDto(
                source.Username,
                source.Role,
                source.Enabled,
                source.CreatedAt,
                source.UpdatedAt));

        TypeAdapterConfig<Channel, ChannelDto>
            .NewConfig()
            .MapWith(source => new ChannelDto(
                source.OwnerUsername,
                source.Id,
                source.Name,
                source.Type,
                source.BaseUrl,
                source.ApiKey,
                source.AuthMode,
                DeserializeObject(source.HeadersJson),
                source.TimeoutSeconds,
                source.RetryCount,
                DeserializeObject(source.CompatJson),
                DeserializeList(source.ModelsJson),
                source.Enabled));

        TypeAdapterConfig<AccessApiKey, AccessApiKeyDto>
            .NewConfig()
            .MapWith(source => new AccessApiKeyDto(
                source.Id,
                source.OwnerUsername,
                source.Name,
                source.KeyPrefix,
                source.KeySuffix,
                $"{source.KeyPrefix}...{source.KeySuffix}",
                source.Enabled,
                source.CreatedAt,
                source.UpdatedAt,
                source.LastUsedAt,
                source.KeyPlaintext));

        TypeAdapterConfig<TavilyKey, TavilyKeyDto>
            .NewConfig()
            .MapWith(source => new TavilyKeyDto(
                source.Id,
                source.Position,
                source.Provider,
                source.ApiKey,
                source.Enabled,
                source.UsageCount,
                source.UsageLimit,
                source.UsageLimit));

        TypeAdapterConfig<RequestLog, RequestLogDto>
            .NewConfig()
            .MapWith(source => new RequestLogDto(
                source.Id,
                source.RequestId,
                source.CreatedAt,
                source.Method,
                source.Path,
                source.ClientIp,
                source.Model,
                source.UpstreamModel,
                source.ChannelId,
                source.IsStream,
                source.TtftMs,
                source.DurationMs,
                source.StatusCode,
                source.InputTokens,
                source.CachedTokens,
                source.OutputTokens,
                source.Cost,
                source.OwnerUsername,
                source.ApiKeyId,
                source.Error,
                source.Detail == null ? null : source.Detail.RequestHeaders,
                source.Detail == null ? null : source.Detail.RequestBody,
                source.Detail == null ? null : source.Detail.UpstreamRequestBody,
                source.Detail == null ? null : source.Detail.UpstreamResponseBody,
                source.Detail == null ? null : source.Detail.ResponseBody,
                source.Detail == null ? null : source.Detail.WebSearchJson,
                RequestStatus(source.StatusCode, source.Error)));

        TypeAdapterConfig<RequestLog, RequestLogEventDto>
            .NewConfig()
            .MapWith(source => new RequestLogEventDto(
                source.Id,
                source.RequestId,
                source.CreatedAt,
                source.Method,
                source.Path,
                source.ClientIp,
                source.Model,
                source.UpstreamModel,
                source.ChannelId,
                source.IsStream,
                source.TtftMs,
                source.DurationMs,
                source.StatusCode,
                source.InputTokens,
                source.CachedTokens,
                source.OutputTokens,
                source.Cost,
                source.OwnerUsername,
                source.ApiKeyId,
                source.Error,
                RequestStatus(source.StatusCode, source.Error)));
    }

    private static Dictionary<string, object?> DeserializeObject(string? raw)
    {
        return DeserializeJson(raw) as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static List<object?> DeserializeList(string? raw)
    {
        return DeserializeJson(raw) as List<object?> ?? [];
    }

    private static object? DeserializeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
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
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static string RequestStatus(int? statusCode, string? error)
    {
        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error) ? "failed" : "success";
    }
}
