using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
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
    private readonly IRepository<Channel> _channelRepository;
    private readonly IRepository<User> _userRepository;

    public ConfigService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IChannelCapacityService channelCapacity,
        IChannelCircuitBreakerService channelCircuitBreaker,
        IWorkContext workContext,
        IRepository<Channel> channelRepository,
        IRepository<User> userRepository)
    {
        _settingsProvider = settingsProvider;
        _channelCapacity = channelCapacity;
        _channelCircuitBreaker = channelCircuitBreaker;
        _workContext = workContext;
        _channelRepository = channelRepository;
        _userRepository = userRepository;
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
        return _channelCapacity.GetActiveRequests(channel.OwnerUsername, channel.Id.ToString());
    }

    private string ResolveHealthStatus(ChannelDto channel)
    {
        return _channelCircuitBreaker.GetHealthStatus(
            channel.OwnerUsername,
            channel.Id.ToString(),
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

        var query = _channelRepository.TableNoTracking;
        Guid? scopeUserId = null;
        if (normalizedOwnerUsername.Length > 0)
        {
            var ownerUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalizedOwnerUsername);
            if (ownerUser is null)
            {
                return [];
            }
            scopeUserId = ownerUser.Id;
            query = query.Where(channel => channel.OwnerUserId == scopeUserId.Value);
        }

        var ordered = scopeUserId.HasValue
            ? query.OrderBy(channel => channel.Position).ThenBy(channel => channel.Id)
            : query.OrderBy(channel => channel.OwnerUserId).ThenBy(channel => channel.Position).ThenBy(channel => channel.Id);

        var channels = ordered.ToList();
        var ownerIds = channels.Select(ch => ch.OwnerUserId).Distinct().ToList();
        var owners = ownerIds.Count > 0
            ? _userRepository.TableNoTracking
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionary(u => u.Id, u => u.Username)
            : new Dictionary<Guid, string>();

        return channels
            .Select(channel => MapToChannelDto(channel,
                owners.TryGetValue(channel.OwnerUserId, out var name) ? name : string.Empty))
            .ToList();
    }

    private static ChannelDto MapToChannelDto(Channel channel, string ownerUsername)
    {
        return new ChannelDto(
            channel.Id,
            channel.OwnerUserId,
            ownerUsername,
            channel.Position,
            channel.Name,
            channel.Type,
            channel.BaseUrl,
            channel.ApiKey,
            channel.AuthMode,
            DeserializeObject(channel.HeadersJson),
            channel.TimeoutSeconds,
            channel.RetryCount,
            channel.Priority,
            channel.Capacity,
            DeserializeObject(channel.CompatJson),
            DeserializeList(channel.ModelsJson),
            channel.Enabled);
    }

    private static IReadOnlyDictionary<string, object?> DeserializeObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static IReadOnlyList<object?> DeserializeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return FromJsonElement(document.RootElement) as List<object?> ?? [];
        }
        catch (JsonException)
        {
            return [];
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

    private void ReplaceChannels(
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

        // 解析 owner username -> userId
        Guid? scopeUserId = null;
        Guid defaultOwnerUserId = Guid.Empty;
        var defaultOwner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalizedDefaultOwner);
        if (defaultOwner is not null)
        {
            defaultOwnerUserId = defaultOwner.Id;
        }
        if (normalizedOwner is not null)
        {
            var scopeUser = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalizedOwner);
            scopeUserId = scopeUser?.Id ?? Guid.Empty;
        }

        // 匹配键:(ownerUserId, Guid id) 保留 CreatedAt/Capacity
        var existingCreated = _channelRepository.TableNoTracking
            .ToDictionary(
                channel => (channel.OwnerUserId, channel.Id),
                channel => channel.CreatedAt);
        var existingCapacities = _channelRepository.TableNoTracking
            .ToDictionary(
                channel => (channel.OwnerUserId, channel.Id),
                channel => channel.Capacity);

        // 删除旧 channel(按 scope)
        var oldChannels = scopeUserId.HasValue && scopeUserId.Value != Guid.Empty
            ? _channelRepository.Table.Where(channel => channel.OwnerUserId == scopeUserId.Value).ToList()
            : _channelRepository.Table.ToList();
        if (oldChannels.Count > 0)
        {
            _channelRepository.Delete(oldChannels);
        }

        var now = UnixTimeSeconds();
        var position = 0;
        var toInsert = new List<Channel>();
        foreach (var channel in channels)
        {
            var channelOwnerUsername = normalizedOwner
                ?? NormalizeUsername(JsonDictionaryValue.Get(channel, "owner_username"));
            if (channelOwnerUsername.Length == 0)
            {
                channelOwnerUsername = normalizedDefaultOwner;
            }

            // owner username -> userId
            var channelOwnerUser = _userRepository.TableNoTracking
                .FirstOrDefault(u => u.Username == channelOwnerUsername);
            var channelOwnerUserId = channelOwnerUser?.Id ?? defaultOwnerUserId;

            var idText = JsonDictionaryValue.String(channel, "id");
            var channelId = Guid.TryParse(idText, out var parsedId) && parsedId != Guid.Empty
                ? parsedId
                : Guid.NewGuid();
            var key = (channelOwnerUserId, channelId);
            var createdAt = existingCreated.TryGetValue(key, out var existingCreatedAt)
                ? existingCreatedAt
                : now;
            var capacity = CapacityValue(channel, existingCapacities, key);
            toInsert.Add(new Channel
            {
                Id = channelId,
                OwnerUserId = channelOwnerUserId,
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

        if (toInsert.Count > 0)
        {
            _channelRepository.Insert(toInsert);
        }
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
        IReadOnlyDictionary<(Guid OwnerUserId, Guid ChannelId), int> existingCapacities,
        (Guid OwnerUserId, Guid ChannelId) key)
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
