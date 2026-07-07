using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyAccessService : IProxyAccessService
{
    private const string RequiredBearerMessage = "valid bearer api key required";
    private static readonly TimeSpan AuthCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IRepository<AccessApiKey> _keyRepository;
    private readonly IRepository<User> _userRepository;
    private readonly ICacheService _cache;

    public ProxyAccessService(
        IRepository<AccessApiKey> keyRepository,
        IRepository<User> userRepository,
        ICacheService cache)
    {
        _keyRepository = keyRepository;
        _userRepository = userRepository;
        _cache = cache;
    }

    public async Task<AuthenticatedAccessApiKeyDto> AuthenticateBearerAsync(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        var authorization = authorizationHeader ?? string.Empty;
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Unauthorized();
        }

        var rawKey = authorization[prefix.Length..].Trim();
        var accessKey = await AuthenticateAccessApiKeyAsync(rawKey);
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

    private async Task<AuthenticatedAccessApiKeyDto?> AuthenticateAccessApiKeyAsync(string? rawKey)
    {
        rawKey = (rawKey ?? string.Empty).Trim();
        if (rawKey.Length == 0)
        {
            return null;
        }

        var hash = OpenCodexSecurity.HashAccessApiKey(rawKey);

        // L1 -> L2 -> DB,逐层回写。apikey 与 user 分两个 key 缓存,
        // 便于在 apikey 变更或 user 变更时各自精准失效,无需前缀扫描。
        var cachedKey = await _cache.GetOrCreateAsync(
            CacheKeys.AuthApiKey(hash),
            () => LoadAccessKeyByHash(hash),
            AuthCacheTtl);

        if (cachedKey is null)
        {
            return null;
        }

        var cachedUser = await _cache.GetOrCreateAsync(
            CacheKeys.AuthUser(cachedKey.OwnerUserId),
            () => LoadUserById(cachedKey.OwnerUserId),
            AuthCacheTtl);

        if (cachedUser is null)
        {
            return null;
        }

        // now 时间戳每次现算,不进缓存,避免把"当前时间"缓存成陈旧值。
        var now = UnixTimeSeconds();
        return new AuthenticatedAccessApiKeyDto(
            cachedKey.Id,
            cachedKey.OwnerUserId,
            cachedUser.Username,
            cachedKey.Name,
            cachedKey.KeyPrefix,
            cachedKey.KeySuffix,
            $"{cachedKey.KeyPrefix}...{cachedKey.KeySuffix}",
            cachedKey.Enabled,
            cachedKey.CreatedAt,
            now,
            now,
            new AccessApiKeyUserDto(cachedUser.Id, cachedUser.Username, cachedUser.Role, cachedUser.Enabled));
    }

    private Task<CachedAccessKey?> LoadAccessKeyByHash(string hash)
    {
        var key = _keyRepository.Table
            .FirstOrDefault(item => item.KeyHash == hash);
        if (key is null || !key.Enabled)
        {
            return Task.FromResult<CachedAccessKey?>(null);
        }

        return Task.FromResult<CachedAccessKey?>(new CachedAccessKey(
            key.Id,
            key.OwnerUserId,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            key.Enabled,
            key.CreatedAt));
    }

    private Task<CachedAccessUser?> LoadUserById(Guid userId)
    {
        var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Id == userId);
        if (owner is null || !owner.Enabled)
        {
            return Task.FromResult<CachedAccessUser?>(null);
        }

        return Task.FromResult<CachedAccessUser?>(new CachedAccessUser(
            owner.Id,
            owner.Username,
            owner.Role,
            owner.Enabled));
    }

    private sealed record CachedAccessKey(
        Guid Id, Guid OwnerUserId, string Name, string KeyPrefix, string KeySuffix, bool Enabled, double CreatedAt);

    private sealed record CachedAccessUser(
        Guid Id, string Username, string Role, bool Enabled);

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
