using System.Globalization;
using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class WebSearchService : IWebSearchService
{
    private const int DefaultWebSearchKeyUsageLimit = 1000;
    private const string KeyUnavailableMessage = "Web Search key is unavailable or has reached its usage limit";

    private static readonly HashSet<string> WebSearchProviders = new(StringComparer.Ordinal)
    {
        "tavily"
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWebSearchClient _webSearchClient;

    public WebSearchService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWebSearchClient webSearchClient)
    {
        _settingsProvider = settingsProvider;
        _webSearchClient = webSearchClient;
    }

    public ApiOpResult<WebSearchConfigResponse> ReadConfig()
    {
        var settings = _settingsProvider.GetSettings();
        return ApiOpResult<WebSearchConfigResponse>.Succeed(WebSearchConfigResponse.From(ReadWebSearchConfig(settings)));
    }

    public ApiOpResult<WebSearchConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        try
        {
            return ApiOpResult<WebSearchConfigResponse>.Succeed(WebSearchConfigResponse.From(ReplaceWebSearchConfig(
                _settingsProvider.GetSettings(),
                body)));
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<WebSearchConfigResponse>.Fail(400, exception.Message);
        }
    }

    private ApiOpResult<TavilyKeyDto> ReserveTestKey(long keyId)
    {
        var key = ReserveTavilyKeyById(_settingsProvider.GetSettings(), keyId);
        return key is null
            ? ApiOpResult<TavilyKeyDto>.Fail(400, KeyUnavailableMessage)
            : ApiOpResult<TavilyKeyDto>.Succeed(key);
    }

    public async Task<ApiOpResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken)
    {
        var reserved = ReserveTestKey(keyId);
        if (!reserved.Succeeded || reserved.Payload is null)
        {
            return ApiOpResult<WebSearchTestKeyResponsePayload>.Fail(400, reserved.Description);
        }

        var result = await _webSearchClient.SearchAsync(
            new WebSearchProviderKey(reserved.Payload.Provider, reserved.Payload.Key),
            query,
            cancellationToken);
        var config = ReadWebSearchConfig(_settingsProvider.GetSettings());
        return ApiOpResult<WebSearchTestKeyResponsePayload>.Succeed(WebSearchTestKeyResponsePayload.From(
            reserved.Payload,
            result,
            config,
            result.DurationMs));
    }

    private static WebSearchConfigDto ReadWebSearchConfig(OpenCodexRuntimeSettings settings)
    {
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var webSearchSettings = context.WebSearchSettings
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == 1);
        var keys = context.TavilyKeys
            .AsNoTracking()
            .OrderBy(key => key.Position)
            .ThenBy(key => key.Id)
            .AsEnumerable()
            .Select(key => key.Adapt<TavilyKeyDto>())
            .ToList();

        return new WebSearchConfigDto(
            webSearchSettings?.Enabled ?? false,
            WebSearchProviders.Order(StringComparer.Ordinal).ToList(),
            DefaultWebSearchKeyUsageLimit,
            keys);
    }

    private static WebSearchConfigDto ReplaceWebSearchConfig(
        OpenCodexRuntimeSettings runtimeSettings,
        IReadOnlyDictionary<string, object?> config)
    {
        var keysValue = JsonDictionaryValue.Get(config, "keys") ?? new List<object?>();
        if (!ConfigValue.TryAsList(keysValue, out var keys))
        {
            throw new ArgumentException("web search keys must be a list", nameof(config));
        }

        using var context = OpenCodexDbContextFactory.Create(runtimeSettings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var now = UnixTimeSeconds();
        var currentDefaultKeyUsageLimit = context.WebSearchSettings
            .AsNoTracking()
            .FirstOrDefault(settings => settings.Id == 1)
            ?.KeyUsageLimit ?? DefaultWebSearchKeyUsageLimit;
        var defaultKeyUsageLimit = ParseRequiredPositiveInt(
            JsonDictionaryValue.Get(config, "key_usage_limit") ?? currentDefaultKeyUsageLimit,
            "web search key_usage_limit");
        var existing = context.TavilyKeys
            .AsNoTracking()
            .ToDictionary(
                key => key.Id,
                key => new ExistingTavilyKey(
                    key.Provider,
                    key.ApiKey,
                    key.UsageCount,
                    key.UsageLimit,
                    key.CreatedAt));

        var settings = context.WebSearchSettings.FirstOrDefault(item => item.Id == 1);
        if (settings is null)
        {
            settings = new WebSearchSettings
            {
                Id = 1,
                CreatedAt = now
            };
            context.WebSearchSettings.Add(settings);
        }

        settings.Enabled = JsonDictionaryValue.Get(config, "enabled") is true;
        settings.KeyUsageLimit = defaultKeyUsageLimit;
        settings.UpdatedAt = now;

        context.TavilyKeys.RemoveRange(context.TavilyKeys);
        context.SaveChanges();

        for (var position = 0; position < keys.Count; position++)
        {
            if (!ConfigValue.TryAsObject(keys[position], out var item))
            {
                throw new ArgumentException($"web search keys[{position + 1}] must be an object", nameof(config));
            }

            var provider = NormalizeWebSearchProvider(JsonDictionaryValue.Get(item, "provider"));
            var apiKey = WebSearchApiKey(item);
            if (apiKey.Length == 0)
            {
                throw new ArgumentException($"web search keys[{position + 1}].key is required", nameof(config));
            }

            var existingId = ParsePositiveLong(JsonDictionaryValue.Get(item, "id"));
            var old = existingId is null ? null : existing.GetValueOrDefault(existingId.Value);
            var usageLimitSource = JsonDictionaryValue.Get(item, "usage_limit")
                ?? JsonDictionaryValue.Get(item, "key_usage_limit");
            var sameKey = old is not null && old.ApiKey == apiKey && old.Provider == provider;
            int usageLimit;
            if (usageLimitSource is null && sameKey)
            {
                usageLimit = old!.UsageLimit;
            }
            else
            {
                usageLimit = ParseRequiredPositiveInt(
                    usageLimitSource ?? defaultKeyUsageLimit,
                    $"web search keys[{position + 1}].usage_limit");
            }

            int usageCount;
            double createdAt;
            if (item.ContainsKey("usage_count"))
            {
                usageCount = ParseRequiredNonNegativeInt(
                    JsonDictionaryValue.Get(item, "usage_count"),
                    $"web search keys[{position + 1}].usage_count");
                createdAt = sameKey ? old!.CreatedAt : now;
            }
            else if (sameKey)
            {
                usageCount = old!.UsageCount;
                createdAt = old.CreatedAt;
            }
            else
            {
                usageCount = 0;
                createdAt = now;
            }

            var row = new TavilyKey
            {
                Position = position,
                Provider = provider,
                ApiKey = apiKey,
                Enabled = JsonDictionaryValue.Get(item, "enabled") is not false,
                UsageCount = usageCount,
                UsageLimit = usageLimit,
                CreatedAt = createdAt,
                UpdatedAt = now
            };
            if (existingId is not null)
            {
                row.Id = existingId.Value;
            }

            context.TavilyKeys.Add(row);
        }

        context.SaveChanges();
        transaction.Commit();
        return ReadWebSearchConfig(runtimeSettings);
    }

    private static TavilyKeyDto? ReserveTavilyKeyById(OpenCodexRuntimeSettings settings, long keyId)
    {
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var reserved = context.TavilyKeys
            .Where(key => key.Id == keyId && key.UsageCount < key.UsageLimit)
            .FirstOrDefault();
        if (reserved is null)
        {
            transaction.Rollback();
            return null;
        }

        reserved.UsageCount += 1;
        reserved.UpdatedAt = UnixTimeSeconds();
        context.SaveChanges();
        transaction.Commit();
        return reserved.Adapt<TavilyKeyDto>();
    }

    private static string NormalizeWebSearchProvider(object? value)
    {
        var provider = (IsPythonFalsy(value) ? "tavily" : value?.ToString() ?? "tavily")
            .Trim()
            .ToLowerInvariant();
        if (!WebSearchProviders.Contains(provider))
        {
            throw new ArgumentException($"unsupported web search provider: {provider}");
        }

        return provider;
    }

    private static string WebSearchApiKey(IReadOnlyDictionary<string, object?> item)
    {
        var value = JsonDictionaryValue.Get(item, "key")
            ?? JsonDictionaryValue.Get(item, "api_key");
        return (value?.ToString() ?? string.Empty).Trim();
    }

    private static int ParseRequiredPositiveInt(object? value, string label)
    {
        var parsed = ToInt(value);
        if (parsed <= 0)
        {
            throw new ArgumentException($"{label} must be a positive integer", label);
        }

        return parsed;
    }

    private static int ParseRequiredNonNegativeInt(object? value, string label)
    {
        var parsed = ToInt(value);
        if (parsed < 0)
        {
            throw new ArgumentException($"{label} must be a non-negative integer", label);
        }

        return parsed;
    }

    private static int ToInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static long? ParsePositiveLong(object? value)
    {
        if (value is bool)
        {
            return null;
        }

        return TryConvertInt64(value, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static bool TryConvertInt64(object? value, out long parsed)
    {
        try
        {
            parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool IsPythonFalsy(object? value)
    {
        return value switch
        {
            null => true,
            string text => text.Length == 0,
            bool boolValue => !boolValue,
            byte byteValue => byteValue == 0,
            sbyte sbyteValue => sbyteValue == 0,
            short shortValue => shortValue == 0,
            ushort ushortValue => ushortValue == 0,
            int intValue => intValue == 0,
            uint uintValue => uintValue == 0,
            long longValue => longValue == 0,
            ulong ulongValue => ulongValue == 0,
            float floatValue => floatValue == 0,
            double doubleValue => doubleValue == 0,
            decimal decimalValue => decimalValue == 0,
            _ => false
        };
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    private sealed class ExistingTavilyKey
    {
        public ExistingTavilyKey(
            string provider,
            string apiKey,
            int usageCount,
            int usageLimit,
            double createdAt)
        {
            Provider = provider;
            ApiKey = apiKey;
            UsageCount = usageCount;
            UsageLimit = usageLimit;
            CreatedAt = createdAt;
        }

        public string Provider { get; }

        public string ApiKey { get; }

        public int UsageCount { get; }

        public int UsageLimit { get; }

        public double CreatedAt { get; }
    }
}
