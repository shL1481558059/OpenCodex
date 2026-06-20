using Mapster;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.ApiKeys;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ApiKeyService : IApiKeyService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWorkContext _workContext;

    public ApiKeyService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWorkContext workContext)
    {
        _settingsProvider = settingsProvider;
        _workContext = workContext;
    }

    public ApiOpResult<ApiKeysResponse> ListKeys(
        string? requestedOwnerUsername)
    {
        var currentUser = _workContext.RequireUser();
        var isSuperadmin = currentUser.Role == "superadmin";
        var ownerUsername = OwnerScope(
            requestedOwnerUsername,
            currentUser.Username,
            isSuperadmin);
        var normalizedOwnerUsername = string.IsNullOrWhiteSpace(ownerUsername)
            ? string.Empty
            : ownerUsername.Trim();
        var settings = _settingsProvider.GetSettings();
        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        var query = context.AccessApiKeys.AsNoTracking();
        if (normalizedOwnerUsername.Length > 0)
        {
            query = query.Where(key => key.OwnerUsername == normalizedOwnerUsername);
        }

        var ordered = normalizedOwnerUsername.Length == 0
            ? query.OrderBy(key => key.OwnerUsername).ThenByDescending(key => key.Id)
            : query.OrderByDescending(key => key.Id);

        return ApiOpResult<ApiKeysResponse>.Succeed(ApiKeysResponse.From(ordered
            .AsEnumerable()
            .Select(key => key.Adapt<AccessApiKeyDto>())
            .ToList()));
    }

    public ApiOpResult<ApiKeyResponsePayload> CreateKey(
        ApiKeyCreateCommand command)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            var owner = command.OwnerUsername.Trim();
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = currentUser.Username;
            }

            if (!isSuperadmin)
            {
                owner = currentUser.Username;
            }

            var key = CreateAccessApiKey(
                _settingsProvider.GetSettings(),
                owner,
                command.Name.Trim());
            return ApiOpResult<ApiKeyResponsePayload>.Succeed(ApiKeyResponsePayload.From(key));
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

    public ApiOpResult<ApiKeyResponsePayload> UpdateKey(
        long keyId,
        ApiKeyUpdateCommand command)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            var key = SetAccessApiKeyEnabled(
                _settingsProvider.GetSettings(),
                keyId,
                command.Enabled,
                OwnerScope(currentUser.Username, isSuperadmin));
            return ApiOpResult<ApiKeyResponsePayload>.Succeed(ApiKeyResponsePayload.From(key));
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<ApiKeyResponsePayload>.Fail(404, exception.Message);
        }
    }

    public ApiOpResult<DeleteApiKeyResponse> DeleteKey(
        long keyId)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            DeleteAccessApiKey(
                _settingsProvider.GetSettings(),
                keyId,
                OwnerScope(currentUser.Username, isSuperadmin));
            return ApiOpResult<DeleteApiKeyResponse>.Succeed(new DeleteApiKeyResponse(true));
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<DeleteApiKeyResponse>.Fail(404, exception.Message);
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

    private static ApiOpResult<ApiKeyResponsePayload> ValidationFailure(string message)
    {
        return ApiOpResult<ApiKeyResponsePayload>.Fail(400, message);
    }

    private static AccessApiKeyDto CreateAccessApiKey(
        OpenCodexRuntimeSettings settings,
        string ownerUsername,
        string name = "")
    {
        ownerUsername = NormalizeUsername(ownerUsername);
        if (ownerUsername.Length == 0)
        {
            throw new ArgumentException("owner_username is required", nameof(ownerUsername));
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        if (!context.Users.Any(user => user.Username == ownerUsername))
        {
            throw new InvalidOperationException("user not found");
        }

        var rawKey = OpenCodexSecurity.GenerateAccessApiKey();
        var now = UnixTimeSeconds();
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

        return key.Adapt<AccessApiKeyDto>();
    }

    private static AccessApiKeyDto SetAccessApiKeyEnabled(
        OpenCodexRuntimeSettings settings,
        long keyId,
        bool enabled,
        string? ownerUsername = null)
    {
        ownerUsername = ownerUsername is null ? null : NormalizeUsername(ownerUsername);

        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
        var query = context.AccessApiKeys.Where(key => key.Id == keyId);
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            query = query.Where(key => key.OwnerUsername == ownerUsername);
        }

        var existing = query.FirstOrDefault()
            ?? throw new InvalidOperationException("api key not found");
        existing.Enabled = enabled;
        existing.UpdatedAt = UnixTimeSeconds();
        context.SaveChanges();
        return existing.Adapt<AccessApiKeyDto>();
    }

    private static void DeleteAccessApiKey(
        OpenCodexRuntimeSettings settings,
        long keyId,
        string? ownerUsername = null)
    {
        ownerUsername = ownerUsername is null ? null : NormalizeUsername(ownerUsername);

        using var context = OpenCodexDbContextFactory.Create(settings.DatabaseProvider, settings.ConnectionString);
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

    private static string NormalizeUsername(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
