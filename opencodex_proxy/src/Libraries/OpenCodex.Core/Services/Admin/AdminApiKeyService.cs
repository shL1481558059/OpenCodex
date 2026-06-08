using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.AdminApiKeys;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Core.Services.Admin;

public sealed class AdminApiKeyService : IAdminApiKeyService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminApiKeyService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ApiResult<ApiKeysResponse> ListKeys(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin)
    {
        var ownerUsername = OwnerScope(
            requestedOwnerUsername,
            currentUsername,
            isSuperadmin);
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();
        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var query = context.AccessApiKeys.AsNoTracking();
        if (normalizedOwnerUsername.Length > 0)
        {
            query = query.Where(key => key.OwnerUsername == normalizedOwnerUsername);
        }

        var ordered = normalizedOwnerUsername.Length == 0
            ? query.OrderBy(key => key.OwnerUsername).ThenByDescending(key => key.Id)
            : query.OrderByDescending(key => key.Id);

        return ApiResult.Success(ApiKeysResponse.From(ordered
            .AsEnumerable()
            .Select(key => EfServiceSupport.ToAccessApiKeyDto(key, includePlaintext: true))
            .ToList()));
    }

    public ApiResult<ApiKeyResponsePayload> CreateKey(
        AdminApiKeyCreateCommand command,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            var owner = command.OwnerUsername.Trim();
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = currentUsername;
            }

            if (!isSuperadmin)
            {
                owner = currentUsername;
            }

            var key = CreateAccessApiKey(
                _settingsProvider.GetSettings(),
                owner,
                command.Name.Trim());
            return ApiResult.Success(ApiKeyResponsePayload.From(key));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ApiResult<ApiKeyResponsePayload> UpdateKey(
        long keyId,
        AdminApiKeyUpdateCommand command,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            var key = SetAccessApiKeyEnabled(
                _settingsProvider.GetSettings(),
                keyId,
                command.Enabled,
                OwnerScope(currentUsername, isSuperadmin));
            return ApiResult.Success(ApiKeyResponsePayload.From(key));
        }
        catch (InvalidOperationException exception)
        {
            return ApiResult.Fail<ApiKeyResponsePayload>(AdminApiKeyErrorCodes.NotFound, exception.Message);
        }
    }

    public ApiResult<DeleteApiKeyResponse> DeleteKey(
        long keyId,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            DeleteAccessApiKey(
                _settingsProvider.GetSettings(),
                keyId,
                OwnerScope(currentUsername, isSuperadmin));
            return ApiResult.Success(new DeleteApiKeyResponse(true));
        }
        catch (InvalidOperationException exception)
        {
            return ApiResult.Fail<DeleteApiKeyResponse>(AdminApiKeyErrorCodes.NotFound, exception.Message);
        }
    }

    private static string? OwnerScope(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin)
    {
        if (!isSuperadmin)
        {
            return currentUsername;
        }

        return string.IsNullOrWhiteSpace(requestedOwnerUsername)
            ? null
            : requestedOwnerUsername.Trim();
    }

    private static string? OwnerScope(string currentUsername, bool isSuperadmin)
    {
        return isSuperadmin ? null : currentUsername;
    }

    private static ApiResult<ApiKeyResponsePayload> ValidationFailure(string message)
    {
        return ApiResult.Fail<ApiKeyResponsePayload>(AdminApiKeyErrorCodes.Validation, message);
    }

    private static AccessApiKeyDto CreateAccessApiKey(
        OpenCodexRuntimeSettings settings,
        string ownerUsername,
        string name = "")
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, ownerUsername);
        ownerUsername = EfServiceSupport.NormalizeUsername(ownerUsername);
        if (ownerUsername.Length == 0)
        {
            throw new ArgumentException("owner_username is required", nameof(ownerUsername));
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        if (!context.Users.Any(user => user.Username == ownerUsername))
        {
            throw new InvalidOperationException("user not found");
        }

        var rawKey = OpenCodexSecurity.GenerateAccessApiKey();
        var now = EfServiceSupport.UnixTimeSeconds();
        var key = new AccessApiKey
        {
            OwnerUsername = ownerUsername,
            Name = (name ?? string.Empty).Trim(),
            KeyHash = OpenCodexSecurity.HashAccessApiKey(rawKey),
            KeyPlaintext = rawKey,
            KeyPrefix = rawKey[..12],
            KeySuffix = rawKey[^6..],
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.AccessApiKeys.Add(key);
        context.SaveChanges();

        return new AccessApiKeyDto(
            key.Id,
            key.OwnerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            $"{key.KeyPrefix}...{key.KeySuffix}",
            key.Enabled,
            key.CreatedAt,
            key.UpdatedAt,
            key.LastUsedAt,
            rawKey);
    }

    private static AccessApiKeyDto SetAccessApiKeyEnabled(
        OpenCodexRuntimeSettings settings,
        long keyId,
        bool enabled,
        string? ownerUsername = null)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, ownerUsername ?? settings.AdminUsername);
        ownerUsername = ownerUsername is null ? null : EfServiceSupport.NormalizeUsername(ownerUsername);

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var query = context.AccessApiKeys.Where(key => key.Id == keyId);
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            query = query.Where(key => key.OwnerUsername == ownerUsername);
        }

        var existing = query.FirstOrDefault()
            ?? throw new InvalidOperationException("api key not found");
        existing.Enabled = enabled;
        existing.UpdatedAt = EfServiceSupport.UnixTimeSeconds();
        context.SaveChanges();
        return EfServiceSupport.ToAccessApiKeyDto(existing, includePlaintext: true);
    }

    private static void DeleteAccessApiKey(
        OpenCodexRuntimeSettings settings,
        long keyId,
        string? ownerUsername = null)
    {
        EfServiceSupport.InitializeDatabase(settings.DbPath, ownerUsername ?? settings.AdminUsername);
        ownerUsername = ownerUsername is null ? null : EfServiceSupport.NormalizeUsername(ownerUsername);

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        var query = context.AccessApiKeys.Where(key => key.Id == keyId);
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            query = query.Where(key => key.OwnerUsername == ownerUsername);
        }

        var existing = query.FirstOrDefault()
            ?? throw new InvalidOperationException("api key not found");
        context.AccessApiKeys.Remove(existing);
        context.SaveChanges();
    }
}
