using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.AdminWebSearch;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminWebSearchService : IAdminWebSearchService
{
    private const int DefaultWebSearchKeyUsageLimit = 1000;
    private const string KeyUnavailableMessage = "Web Search key is unavailable or has reached its usage limit";

    private static readonly HashSet<string> WebSearchProviders = new(StringComparer.Ordinal)
    {
        "tavily"
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWebSearchClient _webSearchClient;

    public AdminWebSearchService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWebSearchClient webSearchClient)
    {
        _settingsProvider = settingsProvider;
        _webSearchClient = webSearchClient;
    }

    public ApiResult<WebSearchConfigResponse> ReadConfig()
    {
        var settings = _settingsProvider.GetSettings();
        return ApiResult.Success(WebSearchConfigResponse.From(ReadWebSearchConfig(settings)));
    }

    public ApiResult<WebSearchConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        try
        {
            return ApiResult.Success(WebSearchConfigResponse.From(ReplaceWebSearchConfig(
                _settingsProvider.GetSettings(),
                body)));
        }
        catch (ArgumentException exception)
        {
            return ApiResult.Fail<WebSearchConfigResponse>(exception.Message);
        }
    }

    private ApiResult<TavilyKeyDto> ReserveTestKey(long keyId)
    {
        var key = ReserveTavilyKeyById(_settingsProvider.GetSettings(), keyId);
        return key is null
            ? ApiResult.Fail<TavilyKeyDto>(KeyUnavailableMessage)
            : ApiResult.Success(key);
    }

    public async Task<ApiResult<WebSearchTestKeyResponsePayload>> TestKeyAsync(
        long keyId,
        string query,
        CancellationToken cancellationToken)
    {
        var reserved = ReserveTestKey(keyId);
        if (!reserved.Succeeded || reserved.Data is null)
        {
            return ApiResult.Fail<WebSearchTestKeyResponsePayload>(reserved.Message);
        }

        var result = await _webSearchClient.SearchAsync(
            new WebSearchProviderKey(reserved.Data.Provider, reserved.Data.Key),
            query,
            cancellationToken);
        var config = ReadWebSearchConfig(_settingsProvider.GetSettings());
        return ApiResult.Success(WebSearchTestKeyResponsePayload.From(
            reserved.Data,
            result,
            config,
            result.DurationMs));
    }

    private static WebSearchConfigDto ReadWebSearchConfig(OpenCodexRuntimeSettings settings)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var webSearchSettings = context.WebSearchSettings
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == 1);
        var keys = context.TavilyKeys
            .AsNoTracking()
            .OrderBy(key => key.Position)
            .ThenBy(key => key.Id)
            .AsEnumerable()
            .Select(EfServiceSupport.ToTavilyKeyDto)
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
        EfServiceSupport.InitializeDatabase(runtimeSettings.DbPath, runtimeSettings.AdminUsername);
        var keysValue = EfServiceSupport.GetOptionalValue(config, "keys") ?? new List<object?>();
        if (!EfServiceSupport.TryAsList(keysValue, out var keys))
        {
            throw new ArgumentException("web search keys must be a list", nameof(config));
        }

        using var context = OpenCodexDbContextFactory.Create(runtimeSettings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var now = EfServiceSupport.UnixTimeSeconds();
        var currentDefaultKeyUsageLimit = context.WebSearchSettings
            .AsNoTracking()
            .FirstOrDefault(settings => settings.Id == 1)
            ?.KeyUsageLimit ?? DefaultWebSearchKeyUsageLimit;
        var defaultKeyUsageLimit = EfServiceSupport.ParseRequiredPositiveInt(
            EfServiceSupport.GetOptionalValue(config, "key_usage_limit") ?? currentDefaultKeyUsageLimit,
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

        settings.Enabled = EfServiceSupport.GetOptionalValue(config, "enabled") is true;
        settings.KeyUsageLimit = defaultKeyUsageLimit;
        settings.UpdatedAt = now;

        context.TavilyKeys.RemoveRange(context.TavilyKeys);
        context.SaveChanges();

        for (var position = 0; position < keys.Count; position++)
        {
            if (!EfServiceSupport.TryAsObject(keys[position], out var item))
            {
                throw new ArgumentException($"web search keys[{position + 1}] must be an object", nameof(config));
            }

            var provider = NormalizeWebSearchProvider(EfServiceSupport.GetOptionalValue(item, "provider"));
            var apiKey = WebSearchApiKey(item);
            if (apiKey.Length == 0)
            {
                throw new ArgumentException($"web search keys[{position + 1}].key is required", nameof(config));
            }

            var existingId = EfServiceSupport.ParsePositiveLong(EfServiceSupport.GetOptionalValue(item, "id"));
            var old = existingId is null ? null : existing.GetValueOrDefault(existingId.Value);
            var usageLimitSource = EfServiceSupport.GetOptionalValue(item, "usage_limit")
                ?? EfServiceSupport.GetOptionalValue(item, "key_usage_limit");
            var sameKey = old is not null && old.ApiKey == apiKey && old.Provider == provider;
            int usageLimit;
            if (usageLimitSource is null && sameKey)
            {
                usageLimit = old!.UsageLimit;
            }
            else
            {
                usageLimit = EfServiceSupport.ParseRequiredPositiveInt(
                    usageLimitSource ?? defaultKeyUsageLimit,
                    $"web search keys[{position + 1}].usage_limit");
            }

            int usageCount;
            double createdAt;
            if (item.ContainsKey("usage_count"))
            {
                usageCount = EfServiceSupport.ParseRequiredNonNegativeInt(
                    EfServiceSupport.GetOptionalValue(item, "usage_count"),
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
                Enabled = !EfServiceSupport.IsExplicitFalse(EfServiceSupport.GetOptionalValue(item, "enabled")),
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
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
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
        reserved.UpdatedAt = EfServiceSupport.UnixTimeSeconds();
        context.SaveChanges();
        transaction.Commit();
        return EfServiceSupport.ToTavilyKeyDto(reserved);
    }

    private static string NormalizeWebSearchProvider(object? value)
    {
        var provider = (EfServiceSupport.IsPythonFalsy(value) ? "tavily" : value?.ToString() ?? "tavily")
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
        var value = EfServiceSupport.GetOptionalValue(item, "key")
            ?? EfServiceSupport.GetOptionalValue(item, "api_key");
        return (value?.ToString() ?? string.Empty).Trim();
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
