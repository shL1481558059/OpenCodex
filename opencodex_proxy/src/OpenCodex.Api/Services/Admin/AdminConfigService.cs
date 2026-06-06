using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Config;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminConfigService : IAdminConfigService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IAdminConfigRepository _config;

    public AdminConfigService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IAdminConfigRepository config)
    {
        _settingsProvider = settingsProvider;
        _config = config;
    }

    public ServiceResult<IReadOnlyList<ChannelRecord>> ReadConfig(
        string currentUsername,
        bool isSuperadmin)
    {
        return ServiceResult.Success(ReadChannels(currentUsername, isSuperadmin));
    }

    public ServiceResult<IReadOnlyList<ChannelRecord>> SaveConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin)
    {
        var saved = SaveChannels(body, currentUsername, isSuperadmin);
        return saved.Succeeded
            ? ServiceResult.Success(ReadChannels(currentUsername, isSuperadmin))
            : ServiceResult.Fail<IReadOnlyList<ChannelRecord>>(saved.Code, saved.Message);
    }

    public ServiceResult<AdminConfigImportResult> ImportConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin)
    {
        if (!ConfigValue.TryAsList(JsonDictionaryValue.Get(body, "channels"), out var importedChannels))
        {
            return ValidationFailure<AdminConfigImportResult>("channels must be a list");
        }

        var currentChannels = ReadChannels(currentUsername, isSuperadmin)
            .Select(ChannelToDictionary)
            .Select(channel => (object?)channel)
            .ToList();
        var currentIds = new HashSet<(string OwnerUsername, string ChannelId)>();
        foreach (var item in currentChannels)
        {
            if (item is IReadOnlyDictionary<string, object?> channel)
            {
                currentIds.Add((
                    ImportOwnerForExisting(channel, currentUsername),
                    JsonDictionaryValue.String(channel, "id")));
            }
        }

        var mergedChannels = currentChannels.Select(CloneJsonValue).ToList();
        var skippedIds = new List<string>();
        foreach (var item in importedChannels)
        {
            if (item is not IReadOnlyDictionary<string, object?> channel)
            {
                mergedChannels.Add(CloneJsonValue(item));
                continue;
            }

            var channelId = JsonDictionaryValue.String(channel, "id");
            var ownerUsername = isSuperadmin
                ? JsonDictionaryValue.String(channel, "owner_username")
                : currentUsername;
            if (ownerUsername.Length == 0)
            {
                ownerUsername = currentUsername;
            }

            var key = (ownerUsername, channelId);
            if (currentIds.Contains(key))
            {
                skippedIds.Add(channelId);
                continue;
            }

            currentIds.Add(key);
            mergedChannels.Add(CloneObject(channel));
        }

        var saved = SaveChannels(
            new Dictionary<string, object?>
            {
                ["channels"] = mergedChannels
            },
            currentUsername,
            isSuperadmin);
        if (!saved.Succeeded)
        {
            return ServiceResult.Fail<AdminConfigImportResult>(saved.Code, saved.Message);
        }

        var config = ReadChannels(currentUsername, isSuperadmin);
        return ServiceResult.Success(new AdminConfigImportResult(
            config,
            mergedChannels.Count - currentChannels.Count,
            skippedIds.Count,
            skippedIds));
    }

    private ServiceResult SaveChannels(
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

            _config.ReplaceChannels(channels!, ownerUsername);
            return ServiceResult.Success();
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

    private IReadOnlyList<ChannelRecord> ReadChannels(
        string currentUsername,
        bool isSuperadmin)
    {
        return _config.ReadChannels(OwnerScope(currentUsername, isSuperadmin));
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
        var copied = CloneObject(candidate);
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

    private static Dictionary<string, object?> ChannelToDictionary(ChannelRecord channel)
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = channel.OwnerUsername,
            ["id"] = channel.Id,
            ["name"] = channel.Name,
            ["type"] = channel.Type,
            ["baseurl"] = channel.BaseUrl,
            ["apikey"] = channel.ApiKey,
            ["auth_mode"] = channel.AuthMode,
            ["headers"] = channel.Headers,
            ["timeout_seconds"] = channel.TimeoutSeconds,
            ["retry_count"] = channel.RetryCount,
            ["compat"] = channel.Compat,
            ["models"] = channel.Models,
            ["enabled"] = channel.Enabled
        };
    }

    private static string ImportOwnerForExisting(
        IReadOnlyDictionary<string, object?> channel,
        string currentUsername)
    {
        var ownerUsername = JsonDictionaryValue.String(channel, "owner_username");
        return ownerUsername.Length == 0 ? currentUsername : ownerUsername;
    }

    private static ServiceResult ValidationFailure(string message)
    {
        return ServiceResult.Fail(AdminConfigErrorCodes.Validation, message);
    }

    private static ServiceResult<T> ValidationFailure<T>(string message)
    {
        return ServiceResult.Fail<T>(AdminConfigErrorCodes.Validation, message);
    }

    private static Dictionary<string, object?> CloneObject(IReadOnlyDictionary<string, object?> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => CloneJsonValue(pair.Value),
            StringComparer.Ordinal);
    }

    private static object? CloneJsonValue(object? value)
    {
        return value switch
        {
            IReadOnlyDictionary<string, object?> dictionary => CloneObject(dictionary),
            IReadOnlyList<object?> list => list.Select(CloneJsonValue).ToList(),
            _ => value
        };
    }
}
