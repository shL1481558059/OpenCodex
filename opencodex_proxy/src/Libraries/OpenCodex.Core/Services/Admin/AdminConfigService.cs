using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.AdminConfig;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminConfigService : IAdminConfigService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminConfigService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiResult<ConfigResponse> ReadConfig(
        string currentUsername,
        bool isSuperadmin)
    {
        return ApiResult.Success(ConfigResponse.From(ReadChannels(currentUsername, isSuperadmin)));
    }

    public ApiResult<ConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin)
    {
        var saved = SaveChannels(body, currentUsername, isSuperadmin);
        return saved.Succeeded
            ? ApiResult.Success(ConfigResponse.From(ReadChannels(currentUsername, isSuperadmin)))
            : ApiResult.Fail<ConfigResponse>(saved.Code, saved.Message);
    }

    public ApiResult<ConfigImportResponse> ImportConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin)
    {
        if (!ConfigValue.TryAsList(JsonDictionaryValue.Get(body, "channels"), out var importedChannels))
        {
            return ValidationFailure<ConfigImportResponse>("channels must be a list");
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
            return ApiResult.Fail<ConfigImportResponse>(saved.Code, saved.Message);
        }

        var config = ReadChannels(currentUsername, isSuperadmin);
        return ApiResult.Success(ConfigImportResponse.From(
            config,
            mergedChannels.Count - currentChannels.Count,
            skippedIds.Count,
            skippedIds));
    }

    public ApiResult<ConfigExportResponse> ExportConfig(
        string currentUsername,
        bool isSuperadmin)
    {
        return ApiResult.Success(ConfigExportResponse.From(ReadChannels(currentUsername, isSuperadmin)));
    }

    private ApiResult SaveChannels(
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
            return ApiResult.Success();
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
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
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
            .Select(EfServiceSupport.ToChannelDto)
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

    private static Dictionary<string, object?> ChannelToDictionary(ChannelDto channel)
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

    private static ApiResult ValidationFailure(string message)
    {
        return ApiResult.Fail(AdminConfigErrorCodes.Validation, message);
    }

    private static ApiResult<T> ValidationFailure<T>(string message)
    {
        return ApiResult.Fail<T>(AdminConfigErrorCodes.Validation, message);
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

    private static void ReplaceChannels(
        OpenCodexRuntimeSettings settings,
        IEnumerable<IReadOnlyDictionary<string, object?>> channels,
        string? ownerUsername)
    {
        var normalizedDefaultOwner = EfServiceSupport.NormalizeUsername(settings.AdminUsername);
        if (normalizedDefaultOwner.Length == 0)
        {
            normalizedDefaultOwner = "admin";
        }

        var normalizedOwner = ownerUsername is null ? null : EfServiceSupport.NormalizeUsername(ownerUsername);
        EfServiceSupport.InitializeDatabase(settings.DbPath, normalizedDefaultOwner);

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var existingCreated = context.Channels
            .AsNoTracking()
            .ToDictionary(
                channel => (channel.OwnerUsername, channel.Id),
                channel => channel.CreatedAt);

        var oldChannels = normalizedOwner is null
            ? context.Channels
            : context.Channels.Where(channel => channel.OwnerUsername == normalizedOwner);
        context.Channels.RemoveRange(oldChannels);
        context.SaveChanges();

        var now = EfServiceSupport.UnixTimeSeconds();
        var position = 0;
        foreach (var channel in channels)
        {
            var channelOwner = normalizedOwner
                ?? EfServiceSupport.NormalizeUsername(EfServiceSupport.GetOptionalValue(channel, "owner_username"))
                ?? normalizedDefaultOwner;
            if (channelOwner.Length == 0)
            {
                channelOwner = normalizedDefaultOwner;
            }

            var id = EfServiceSupport.RequiredString(channel, "id");
            var key = (channelOwner, id);
            var createdAt = existingCreated.TryGetValue(key, out var existingCreatedAt)
                ? existingCreatedAt
                : now;
            context.Channels.Add(new Channel
            {
                OwnerUsername = channelOwner,
                Id = id,
                Position = position,
                Name = EfServiceSupport.OptionalString(channel, "name", string.Empty),
                Type = EfServiceSupport.RequiredString(channel, "type"),
                BaseUrl = EfServiceSupport.RequiredString(channel, "baseurl"),
                ApiKey = EfServiceSupport.OptionalString(channel, "apikey", string.Empty),
                AuthMode = EfServiceSupport.OptionalString(channel, "auth_mode", "config"),
                HeadersJson = EfServiceSupport.JsonDumps(
                    EfServiceSupport.GetOptionalValue(channel, "headers") ?? new Dictionary<string, object?>()),
                TimeoutSeconds = EfServiceSupport.TimeoutValue(
                    EfServiceSupport.GetOptionalValue(channel, "timeout_seconds"),
                    settings.DefaultTimeout),
                RetryCount = EfServiceSupport.RetryCountValue(channel),
                CompatJson = EfServiceSupport.JsonDumps(
                    EfServiceSupport.GetOptionalValue(channel, "compat") ?? new Dictionary<string, object?>()),
                ModelsJson = EfServiceSupport.JsonDumps(
                    EfServiceSupport.GetOptionalValue(channel, "models") ?? new List<object?>()),
                Enabled = !EfServiceSupport.IsExplicitFalse(EfServiceSupport.GetOptionalValue(channel, "enabled")),
                CreatedAt = createdAt,
                UpdatedAt = now
            });
            position++;
        }

        context.SaveChanges();
        transaction.Commit();
    }
}
