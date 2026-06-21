using System.Text.Json;
using Mapster;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services.Mapping;

public static class OpenCodexMappingConfig
{
    public static void Register()
    {
        TypeAdapterConfig<User, UserDto>
            .NewConfig()
            .MapWith(source => new UserDto(
                source.Id,
                source.Username,
                source.Role,
                source.Enabled,
                source.CreatedAt,
                source.UpdatedAt));

        TypeAdapterConfig<Channel, ChannelDto>
            .NewConfig()
            .MapWith(source => new ChannelDto(
                source.Id,
                source.OwnerUserId,
                string.Empty,
                source.Position,
                source.Name,
                source.Type,
                source.BaseUrl,
                source.ApiKey,
                source.AuthMode,
                DeserializeObject(source.HeadersJson),
                source.TimeoutSeconds,
                source.RetryCount,
                source.Priority,
                source.Capacity,
                DeserializeObject(source.CompatJson),
                DeserializeList(source.ModelsJson),
               source.Enabled));

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

        TypeAdapterConfig<ModelPricing, ModelPricingDto>
            .NewConfig()
            .MapWith(source => new ModelPricingDto(
                source.Id,
                source.ModelId,
                source.Vendor,
                source.Name,
                source.MatchPattern,
                source.InputPrice,
                source.CachedInputPrice,
                source.OutputPrice,
                source.Enabled,
                source.Source,
               source.CreatedAt,
               source.UpdatedAt));

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

    private static string RequestStatus(string? lifecycleStatus, int? statusCode, string? error)
    {
        if (lifecycleStatus is not null)
        {
            return lifecycleStatus;
        }

        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error)
            ? ProxyRequestLifecycleStatus.Failed
            : ProxyRequestLifecycleStatus.Success;
    }
}
