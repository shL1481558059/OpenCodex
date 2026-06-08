using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyAccessService : IProxyAccessService
{
    private const string RequiredBearerMessage = "valid bearer api key required";

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyAccessService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public AuthenticatedAccessApiKeyDto AuthenticateBearer(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        var authorization = authorizationHeader ?? string.Empty;
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Unauthorized();
        }

        var rawKey = authorization[prefix.Length..].Trim();
        var accessKey = AuthenticateAccessApiKey(rawKey);
        if (accessKey is null)
        {
            throw Unauthorized();
        }

        return accessKey;
    }

    private static BadRequestException Unauthorized()
    {
        return new BadRequestException(RequiredBearerMessage, ProxyHttpStatus.Unauthorized);
    }

    private AuthenticatedAccessApiKeyDto? AuthenticateAccessApiKey(string? rawKey)
    {
        rawKey = (rawKey ?? string.Empty).Trim();
        if (rawKey.Length == 0)
        {
            return null;
        }

        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var hash = OpenCodexSecurity.HashAccessApiKey(rawKey);
        var key = context.AccessApiKeys
            .Include(item => item.Owner)
            .FirstOrDefault(item => item.KeyHash == hash);
        if (key is null || !key.Enabled || key.Owner is null || !key.Owner.Enabled)
        {
            transaction.Rollback();
            return null;
        }

        var now = EfServiceSupport.UnixTimeSeconds();
        key.LastUsedAt = now;
        key.UpdatedAt = now;
        context.SaveChanges();
        transaction.Commit();

        return new AuthenticatedAccessApiKeyDto(
            key.Id,
            key.OwnerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            $"{key.KeyPrefix}...{key.KeySuffix}",
            key.Enabled,
            key.CreatedAt,
            now,
            now,
            new AccessApiKeyUserDto(key.OwnerUsername, key.Owner.Role, key.Owner.Enabled));
    }
}
