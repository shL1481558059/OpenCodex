using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyAccessService : IProxyAccessService
{
    private const string RequiredBearerMessage = "valid bearer api key required";

    private readonly IRepository<AccessApiKey> _keyRepository;
    private readonly IRepository<User> _userRepository;

    public ProxyAccessService(
        IRepository<AccessApiKey> keyRepository,
        IRepository<User> userRepository)
    {
        _keyRepository = keyRepository;
        _userRepository = userRepository;
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

        var hash = OpenCodexSecurity.HashAccessApiKey(rawKey);
        var key = _keyRepository.Table
            .FirstOrDefault(item => item.KeyHash == hash);
        if (key is null || !key.Enabled)
        {
            return null;
        }

        // 手动 join User(禁止导航属性)
        var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Id == key.OwnerUserId);
        if (owner is null || !owner.Enabled)
        {
            return null;
        }

        var now = UnixTimeSeconds();
        // 暂时禁用 LastUsedAt 更新以避免并发问题
        // key.LastUsedAt = now;
        // key.UpdatedAt = now;
        // _keyRepository.Update(key);

        return new AuthenticatedAccessApiKeyDto(
            key.Id,
            key.OwnerUserId,
            owner.Username,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            $"{key.KeyPrefix}...{key.KeySuffix}",
            key.Enabled,
            key.CreatedAt,
            now,
            now,
            new AccessApiKeyUserDto(owner.Id, owner.Username, owner.Role, owner.Enabled));
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
