using System.Globalization;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
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

    private readonly IWebSearchClient _webSearchClient;
    private readonly IRepository<WebSearchSettings> _settingsRepository;
    private readonly IRepository<TavilyKey> _keyRepository;

    public WebSearchService(
        IWebSearchClient webSearchClient,
        IRepository<WebSearchSettings> settingsRepository,
        IRepository<TavilyKey> keyRepository)
    {
        _webSearchClient = webSearchClient;
        _settingsRepository = settingsRepository;
        _keyRepository = keyRepository;
    }

    public ApiOpResult<WebSearchConfigResponse> ReadConfig()
    {
        return ApiOpResult<WebSearchConfigResponse>.Succeed(WebSearchConfigResponse.From(ReadWebSearchConfig()));
    }

    public ApiOpResult<WebSearchConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        try
        {
            return ApiOpResult<WebSearchConfigResponse>.Succeed(WebSearchConfigResponse.From(ReplaceWebSearchConfig(body)));
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<WebSearchConfigResponse>.Fail(400, exception.Message);
        }
    }

    public ApiOpResult<WebSearchConfigResponse> ImportConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        try
        {
            return ApiOpResult<WebSearchConfigResponse>.Succeed(WebSearchConfigResponse.From(MergeWebSearchConfig(body)));
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<WebSearchConfigResponse>.Fail(400, exception.Message);
        }
    }

    private ApiOpResult<TavilyKeyDto> ReserveTestKey(Guid keyId)
    {
        var key = ReserveTavilyKeyById(keyId);
        return key is null
            ? ApiOpResult<TavilyKeyDto>.Fail(400, KeyUnavailableMessage)
            : ApiOpResult<TavilyKeyDto>.Succeed(key);
    }

    public async Task<ApiOpResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        Guid keyId,
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
        var config = ReadWebSearchConfig();
        return ApiOpResult<WebSearchTestKeyResponsePayload>.Succeed(WebSearchTestKeyResponsePayload.From(
            reserved.Payload,
            result,
            config,
            result.DurationMs));
    }

    private WebSearchConfigDto ReadWebSearchConfig()
    {
        var webSearchSettings = _settingsRepository.TableNoTracking.FirstOrDefault();
        var keys = _keyRepository.TableNoTracking
            .OrderBy(key => key.Position)
            .ThenBy(key => key.Id)
            .AsEnumerable()
            .Select(MapToDto)
            .ToList();

        return new WebSearchConfigDto(
            webSearchSettings?.Enabled ?? false,
            WebSearchProviders.Order(StringComparer.Ordinal).ToList(),
            DefaultWebSearchKeyUsageLimit,
            keys);
    }

    private WebSearchConfigDto ReplaceWebSearchConfig(
        IReadOnlyDictionary<string, object?> config)
    {
        var keysValue = JsonDictionaryValue.Get(config, "keys") ?? new List<object?>();
        if (!ConfigValue.TryAsList(keysValue, out var keys))
        {
            throw new ArgumentException("web search keys must be a list", nameof(config));
        }

        var now = UnixTimeSeconds();
        var currentDefaultKeyUsageLimit = _settingsRepository.TableNoTracking.FirstOrDefault()
            ?.KeyUsageLimit ?? DefaultWebSearchKeyUsageLimit;
        var defaultKeyUsageLimit = ParseRequiredPositiveInt(
            JsonDictionaryValue.Get(config, "key_usage_limit") ?? currentDefaultKeyUsageLimit,
            "web search key_usage_limit");
        var existing = _keyRepository.TableNoTracking
            .ToDictionary(
                key => key.Id,
                key => new ExistingTavilyKey(
                    key.Provider,
                    key.ApiKey,
                    key.UsageCount,
                    key.UsageLimit,
                    key.CreatedAt));

        var settings = _settingsRepository.Table.FirstOrDefault();
        if (settings is null)
        {
            settings = new WebSearchSettings
            {
                CreatedAt = now
            };
            _settingsRepository.Insert(settings);
        }

        settings.Enabled = JsonDictionaryValue.Get(config, "enabled") is true;
        settings.KeyUsageLimit = defaultKeyUsageLimit;
        settings.UpdatedAt = now;
        _settingsRepository.Update(settings);

       // 先删除全部旧 key,再插入新 key(不考虑历史数据,接受非原子)
       var oldKeys = _keyRepository.Table.ToList();
       if (oldKeys.Count > 0)
       {
           _keyRepository.Delete(oldKeys);
       }

        var newKeys = BuildNewKeys(keys, existing, defaultKeyUsageLimit, now);
        if (newKeys.Count > 0)
        {
            _keyRepository.Insert(newKeys);
        }

        return ReadWebSearchConfig();
    }

    private WebSearchConfigDto MergeWebSearchConfig(
        IReadOnlyDictionary<string, object?> config)
    {
        var keysValue = JsonDictionaryValue.Get(config, "keys") ?? new List<object?>();
        if (!ConfigValue.TryAsList(keysValue, out var keys))
        {
            throw new ArgumentException("web search keys must be a list", nameof(config));
        }

        var now = UnixTimeSeconds();
        var currentDefaultKeyUsageLimit = _settingsRepository.TableNoTracking.FirstOrDefault()
            ?.KeyUsageLimit ?? DefaultWebSearchKeyUsageLimit;
        var defaultKeyUsageLimit = ParseRequiredPositiveInt(
            JsonDictionaryValue.Get(config, "key_usage_limit") ?? currentDefaultKeyUsageLimit,
            "web search key_usage_limit");

        var settings = _settingsRepository.Table.FirstOrDefault();
        if (settings is null)
        {
            settings = new WebSearchSettings
            {
                CreatedAt = now
            };
            _settingsRepository.Insert(settings);
        }

        settings.Enabled = JsonDictionaryValue.Get(config, "enabled") is true;
        settings.KeyUsageLimit = defaultKeyUsageLimit;
        settings.UpdatedAt = now;
        _settingsRepository.Update(settings);

        var existingKeys = _keyRepository.Table.ToList();
        // 合并键:(provider, api_key)
        var existingByKey = existingKeys.ToDictionary(
            key => (key.Provider, key.ApiKey),
            key => key);

        var nextPosition = existingKeys.Count > 0
            ? existingKeys.Max(key => key.Position) + 1
            : 0;
        var toInsert = new List<TavilyKey>();
        var toUpdate = new List<TavilyKey>();

        for (var index = 0; index < keys.Count; index++)
        {
            if (!ConfigValue.TryAsObject(keys[index], out var item))
            {
                throw new ArgumentException($"web search keys[{index + 1}] must be an object", nameof(keys));
            }

            var provider = NormalizeWebSearchProvider(JsonDictionaryValue.Get(item, "provider"));
            var apiKey = WebSearchApiKey(item);
            if (apiKey.Length == 0)
            {
                throw new ArgumentException($"web search keys[{index + 1}].key is required", nameof(keys));
            }

            var matchKey = (provider, apiKey);
            var usageLimitSource = JsonDictionaryValue.Get(item, "usage_limit")
                ?? JsonDictionaryValue.Get(item, "key_usage_limit");
            var usageCountValue = JsonDictionaryValue.Get(item, "usage_count");

            if (existingByKey.TryGetValue(matchKey, out var existing))
            {
                existing.Enabled = JsonDictionaryValue.Get(item, "enabled") is not false;
                if (usageLimitSource is not null)
                {
                    existing.UsageLimit = ParseRequiredPositiveInt(usageLimitSource, $"web search keys[{index + 1}].usage_limit");
                }
                if (usageCountValue is not null)
                {
                    existing.UsageCount = ParseRequiredNonNegativeInt(usageCountValue, $"web search keys[{index + 1}].usage_count");
                }
                existing.UpdatedAt = now;
                toUpdate.Add(existing);
            }
            else
            {
                var usageLimit = usageLimitSource is not null
                    ? ParseRequiredPositiveInt(usageLimitSource, $"web search keys[{index + 1}].usage_limit")
                    : defaultKeyUsageLimit;
                var usageCount = usageCountValue is not null
                    ? ParseRequiredNonNegativeInt(usageCountValue, $"web search keys[{index + 1}].usage_count")
                    : 0;
                toInsert.Add(new TavilyKey
                {
                    Position = nextPosition++,
                    Provider = provider,
                    ApiKey = apiKey,
                    Enabled = JsonDictionaryValue.Get(item, "enabled") is not false,
                    UsageCount = usageCount,
                    UsageLimit = usageLimit,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        foreach (var key in toUpdate)
        {
            _keyRepository.Update(key);
        }

        if (toInsert.Count > 0)
        {
            _keyRepository.Insert(toInsert);
        }

        return ReadWebSearchConfig();
    }

    private List<TavilyKey> BuildNewKeys(
        IReadOnlyList<object?> keys,
        Dictionary<Guid, ExistingTavilyKey> existing,
        int defaultKeyUsageLimit,
        double now)
    {
        var result = new List<TavilyKey>();
        for (var position = 0; position < keys.Count; position++)
        {
            if (!ConfigValue.TryAsObject(keys[position], out var item))
            {
                throw new ArgumentException($"web search keys[{position + 1}] must be an object", nameof(keys));
            }

            var provider = NormalizeWebSearchProvider(JsonDictionaryValue.Get(item, "provider"));
            var apiKey = WebSearchApiKey(item);
            if (apiKey.Length == 0)
            {
                throw new ArgumentException($"web search keys[{position + 1}].key is required", nameof(keys));
            }

            var existingId = ParsePositiveGuid(JsonDictionaryValue.Get(item, "id"));
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

            result.Add(new TavilyKey
            {
                Position = position,
                Provider = provider,
                ApiKey = apiKey,
                Enabled = JsonDictionaryValue.Get(item, "enabled") is not false,
                UsageCount = usageCount,
                UsageLimit = usageLimit,
                CreatedAt = createdAt,
                UpdatedAt = now
            });
        }

        return result;
    }

    private TavilyKeyDto? ReserveTavilyKeyById(Guid keyId)
    {
        var reserved = _keyRepository.Table
            .Where(key => key.Id == keyId && key.UsageCount < key.UsageLimit)
            .FirstOrDefault();
        if (reserved is null)
        {
            return null;
        }

        reserved.UsageCount += 1;
        reserved.UpdatedAt = UnixTimeSeconds();
        _keyRepository.Update(reserved);
        return MapToDto(reserved);
    }

    private static TavilyKeyDto MapToDto(TavilyKey key)
    {
        return new TavilyKeyDto(
            key.Id,
            key.Position,
            key.Provider,
            key.ApiKey,
            key.Enabled,
            key.UsageCount,
            key.UsageLimit,
            key.UsageLimit);
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

    private static Guid? ParsePositiveGuid(object? value)
    {
        if (value is bool || value is null)
        {
            return null;
        }

        var text = value.ToString();
        if (Guid.TryParse(text, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        return null;
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
