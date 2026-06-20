using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services;

public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IChannelCapacityService _channelCapacity;
    private readonly IChannelCircuitBreakerService _channelCircuitBreaker;
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWorkContext _workContext;

    public ConfigService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IChannelCapacityService channelCapacity,
        IChannelCircuitBreakerService channelCircuitBreaker,
        IWorkContext workContext)
    {
        _settingsProvider = settingsProvider;
        _channelCapacity = channelCapacity;
        _channelCircuitBreaker = channelCircuitBreaker;
        _workContext = workContext;
    }

    public ApiOpResult<ConfigResponse> ReadConfig()
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        return ApiOpResult<ConfigResponse>.Succeed(ConfigResponse.From(
            ReadChannels(currentUsername, isSuperadmin),
            ResolveActiveRequests,
            ResolveHealthStatus));
    }

    public ApiOpResult<ConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var saved = SaveChannels(body, currentUsername, isSuperadmin);
        return saved.Succeeded
            ? ApiOpResult<ConfigResponse>.Succeed(ConfigResponse.From(
                ReadChannels(currentUsername, isSuperadmin),
                ResolveActiveRequests,
                ResolveHealthStatus))
            : ApiOpResult<ConfigResponse>.Fail(saved.Code, saved.Description);
    }

    private int ResolveActiveRequests(ChannelDto channel)
    {
        return _channelCapacity.GetActiveRequests(channel.OwnerUsername, channel.Id);
    }

    private string ResolveHealthStatus(ChannelDto channel)
    {
        return _channelCircuitBreaker.GetHealthStatus(
            channel.OwnerUsername,
            channel.Id,
            channel.Enabled) switch
        {
            ChannelHealthStatus.Disabled => "disabled",
            ChannelHealthStatus.Open => "open",
            ChannelHealthStatus.HalfOpen => "half_open",
            _ => "healthy"
        };
    }

    private (string Username, bool IsSuperadmin) CurrentScope()
    {
        var currentUser = _workContext.RequireUser();
        return (currentUser.Username, currentUser.Role == "superadmin");
    }

    private ApiOpResult SaveChannels(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            var ownerUsername = OwnerScope(currentUsername, isSuperadmin);
            var settings = _settingsProvider.GetSettings();
            var normalized = ConfigNormalizer.Normalize(WithEffectiveOwner(
                body,
                ownerUsername,
                settings.AdminUsername));
            ConfigValidator.Validate(normalized, settings.DefaultTimeout);
            var channels = JsonDictionaryValue.List(normalized, "channels")
                .Select(item => item as IReadOnlyDictionary<string, object?>)
                .ToList();
            if (channels.Any(item => item is null))
            {
                return ValidationFailure("each channel must be an object");
            }

            ReplaceChannels(
                settings,
                channels.Cast<IReadOnlyDictionary<string, object?>>(),
                ownerUsername);
            return ApiOpResult.Succeed();
        }
        catch (ConfigException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    private IReadOnlyList<ChannelDto> ReadChannels(
        string currentUsername,
        bool isSuperadmin)
    {
        var ownerUsername = OwnerScope(currentUsername, isSuperadmin);
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();
        var settings = _settingsProvider.GetSettings();
        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        var query = context.Channels.AsNoTracking();
        if (normalizedOwnerUsername.Length > 0)
        {
            query = query.Where(channel => channel.OwnerUsername == normalizedOwnerUsername);
        }

        var ordered = normalizedOwnerUsername.Length > 0
            ? query.OrderBy(channel => channel.Position).ThenBy(channel => channel.Id)
            : query.OrderBy(channel => channel.OwnerUsername).ThenBy(channel => channel.Position).ThenBy(channel => channel.Id);

        return ordered
            .AsEnumerable()
            .Select(channel => channel.Adapt<ChannelDto>())
            .ToList();
    }

    private static string? OwnerScope(string currentUsername, bool isSuperadmin)
    {
        return isSuperadmin ? null : currentUsername;
    }

    private static Dictionary<string, object?> WithEffectiveOwner(
        IReadOnlyDictionary<string, object?> candidate,
        string? ownerUsername,
        string adminUsername)
    {
        var copied = WebSearchPayload.DeepCopyObject(candidate);
        var channels = JsonDictionaryValue.List(copied, "channels");
        foreach (var item in channels)
        {
            if (item is not Dictionary<string, object?> channel)
            {
                continue;
            }

            var explicitOwner = JsonDictionaryValue.String(channel, "owner_username");
            channel["owner_username"] = ownerUsername ?? (explicitOwner.Length == 0 ? adminUsername : explicitOwner);
            if (channel["owner_username"] is string owner && owner.Trim().Length == 0)
            {
                channel["owner_username"] = adminUsername;
            }
        }

        return copied;
    }

    private static ApiOpResult ValidationFailure(string message)
    {
        return ApiOpResult.Fail(400, message);
    }

    private static void ReplaceChannels(
        OpenCodexRuntimeSettings settings,
        IEnumerable<IReadOnlyDictionary<string, object?>> channels,
        string? ownerUsername)
    {
        var normalizedDefaultOwner = NormalizeUsername(settings.AdminUsername);
        if (normalizedDefaultOwner.Length == 0)
        {
            normalizedDefaultOwner = "admin";
        }

        var normalizedOwner = ownerUsername is null ? null : NormalizeUsername(ownerUsername);

        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        using var transaction = context.Database.BeginTransaction();
        var existingCreated = context.Channels
            .AsNoTracking()
            .ToDictionary(
                channel => (channel.OwnerUsername, channel.Id),
                channel => channel.CreatedAt);
        var existingCapacities = context.Channels
            .AsNoTracking()
            .ToDictionary(
                channel => (channel.OwnerUsername, channel.Id),
                channel => channel.Capacity);

        var oldChannels = normalizedOwner is null
            ? context.Channels
            : context.Channels.Where(channel => channel.OwnerUsername == normalizedOwner);
        context.Channels.RemoveRange(oldChannels);
        context.SaveChanges();

        var now = UnixTimeSeconds();
        var position = 0;
        foreach (var channel in channels)
        {
            var channelOwner = normalizedOwner
                ?? NormalizeUsername(JsonDictionaryValue.Get(channel, "owner_username"));
            if (channelOwner.Length == 0)
            {
                channelOwner = normalizedDefaultOwner;
            }

            var id = JsonDictionaryValue.String(channel, "id");
            var key = (channelOwner, id);
            var createdAt = existingCreated.TryGetValue(key, out var existingCreatedAt)
                ? existingCreatedAt
                : now;
            var capacity = CapacityValue(channel, existingCapacities, key);
            context.Channels.Add(new Channel
            {
                OwnerUsername = channelOwner,
                Id = id,
                Position = position,
                Name = JsonDictionaryValue.String(channel, "name"),
                Type = JsonDictionaryValue.String(channel, "type"),
                BaseUrl = JsonDictionaryValue.String(channel, "baseurl"),
                ApiKey = JsonDictionaryValue.String(channel, "apikey"),
                AuthMode = StringOrDefault(channel, "auth_mode", "config"),
                HeadersJson = JsonSerializer.Serialize(
                    JsonDictionaryValue.Get(channel, "headers") ?? new Dictionary<string, object?>(),
                    JsonOptions),
                TimeoutSeconds = TimeoutValue(
                    JsonDictionaryValue.Get(channel, "timeout_seconds"),
                    settings.DefaultTimeout),
                RetryCount = RetryCountValue(channel),
                Priority = PriorityValue(channel, position),
                Capacity = capacity,
                CompatJson = JsonSerializer.Serialize(
                    JsonDictionaryValue.Get(channel, "compat") ?? new Dictionary<string, object?>(),
                    JsonOptions),
                ModelsJson = JsonSerializer.Serialize(
                    JsonDictionaryValue.Get(channel, "models") ?? new List<object?>(),
                    JsonOptions),
                Enabled = JsonDictionaryValue.Get(channel, "enabled") is not false,
                CreatedAt = createdAt,
                UpdatedAt = now
            });
            position++;
        }

        context.SaveChanges();
        transaction.Commit();
    }

    private static string NormalizeUsername(object? value)
    {
        return (value?.ToString() ?? string.Empty).Trim();
    }

    private static string StringOrDefault(
        IReadOnlyDictionary<string, object?> dictionary,
        string key,
        string defaultValue)
    {
        var value = JsonDictionaryValue.Get(dictionary, key);
        return value is null ? defaultValue : (value.ToString() ?? defaultValue).Trim();
    }

    private static int TimeoutValue(object? value, int defaultTimeout)
    {
        return IsPythonFalsy(value) ? defaultTimeout : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static int RetryCountValue(IReadOnlyDictionary<string, object?> channel)
    {
        return Convert.ToInt32(
            channel.TryGetValue("retry_count", out var value) ? value : OpenCodexConfig.DefaultRetryCount,
            CultureInfo.InvariantCulture);
    }

    private static int PriorityValue(IReadOnlyDictionary<string, object?> channel, int fallbackPriority)
    {
        return Convert.ToInt32(
            channel.TryGetValue("priority", out var value) ? value : fallbackPriority,
            CultureInfo.InvariantCulture);
    }

    private static int CapacityValue(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<(string OwnerUsername, string ChannelId), int> existingCapacities,
        (string OwnerUsername, string ChannelId) key)
    {
        if (!channel.TryGetValue("capacity", out var value) || value is null)
        {
            if (existingCapacities.TryGetValue(key, out var existingCapacity))
            {
                return existingCapacity;
            }

            throw new ArgumentException($"channel {key.ChannelId} capacity is required");
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
}
