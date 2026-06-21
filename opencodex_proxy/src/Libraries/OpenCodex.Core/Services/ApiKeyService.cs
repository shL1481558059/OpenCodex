using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.ApiKeys;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ApiKeyService : IApiKeyService
{
    private readonly IWorkContext _workContext;
    private readonly IRepository<AccessApiKey> _keyRepository;
    private readonly IRepository<User> _userRepository;

    public ApiKeyService(
        IWorkContext workContext,
        IRepository<AccessApiKey> keyRepository,
        IRepository<User> userRepository)
    {
        _workContext = workContext;
        _keyRepository = keyRepository;
        _userRepository = userRepository;
    }

    public ApiOpResult<ApiKeysResponse> ListKeys(
        string? requestedOwnerUsername)
    {
        var currentUser = _workContext.RequireUser();
        var isSuperadmin = currentUser.Role == "superadmin";
        var scopeUsername = OwnerScope(requestedOwnerUsername, currentUser.Username, isSuperadmin);

        var query = _keyRepository.TableNoTracking;

        // 非 superadmin 只能看自己的 key;superadmin 未指定 owner 时看全部
        Guid? scopeUserId;
        if (!isSuperadmin)
        {
            scopeUserId = currentUser.UserId;
        }
        else if (!string.IsNullOrWhiteSpace(scopeUsername))
        {
            var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == scopeUsername!.Trim());
            scopeUserId = owner?.Id ?? Guid.Empty;
        }
        else
        {
            scopeUserId = null;
        }

        if (scopeUserId.HasValue && scopeUserId.Value != Guid.Empty)
        {
            query = query.Where(key => key.OwnerUserId == scopeUserId.Value);
        }

        var ordered = scopeUserId.HasValue
            ? query.OrderByDescending(key => key.Id)
            : query.OrderBy(key => key.OwnerUserId).ThenByDescending(key => key.Id);

        var keys = ordered.ToList();
        var ownerIds = keys.Select(k => k.OwnerUserId).Distinct().ToList();
        var owners = ownerIds.Count > 0
            ? _userRepository.TableNoTracking
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionary(u => u.Id, u => u.Username)
            : new Dictionary<Guid, string>();

        var dtos = keys
            .Select(key => MapToDto(key, owners.TryGetValue(key.OwnerUserId, out var name) ? name : string.Empty))
            .ToList();
        return ApiOpResult<ApiKeysResponse>.Succeed(ApiKeysResponse.From(dtos));
    }

    public ApiOpResult<ApiKeyResponsePayload> CreateKey(
        ApiKeyCreateCommand command)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            var ownerUserId = command.OwnerUserId;
            if (ownerUserId == Guid.Empty)
            {
                ownerUserId = currentUser.UserId;
            }
            if (!isSuperadmin)
            {
                ownerUserId = currentUser.UserId;
            }

            var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Id == ownerUserId);
            if (owner is null)
            {
                throw new InvalidOperationException("user not found");
            }

            var rawKey = OpenCodexSecurity.GenerateAccessApiKey();
            var now = UnixTimeSeconds();
            var key = new AccessApiKey
            {
                OwnerUserId = ownerUserId,
                Name = (command.Name ?? string.Empty).Trim(),
                KeyHash = OpenCodexSecurity.HashAccessApiKey(rawKey),
                KeyPlaintext = rawKey,
                KeyPrefix = rawKey[..12],
                KeySuffix = rawKey[^6..],
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            _keyRepository.Insert(key);

            return ApiOpResult<ApiKeyResponsePayload>.Succeed(
                ApiKeyResponsePayload.From(MapToDto(key, owner.Username)));
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
        Guid keyId,
        ApiKeyUpdateCommand command)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            var scopeUserId = isSuperadmin ? (Guid?)null : currentUser.UserId;

            var query = _keyRepository.Table.Where(key => key.Id == keyId);
            if (scopeUserId.HasValue)
            {
                query = query.Where(key => key.OwnerUserId == scopeUserId.Value);
            }

            var existing = query.FirstOrDefault()
                ?? throw new InvalidOperationException("api key not found");
            existing.Enabled = command.Enabled;
            existing.UpdatedAt = UnixTimeSeconds();
            _keyRepository.Update(existing);

            var owner = _userRepository.TableNoTracking.FirstOrDefault(u => u.Id == existing.OwnerUserId);
            return ApiOpResult<ApiKeyResponsePayload>.Succeed(
                ApiKeyResponsePayload.From(MapToDto(existing, owner?.Username ?? string.Empty)));
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<ApiKeyResponsePayload>.Fail(404, exception.Message);
        }
    }

    public ApiOpResult<DeleteApiKeyResponse> DeleteKey(
        Guid keyId)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var isSuperadmin = currentUser.Role == "superadmin";
            var scopeUserId = isSuperadmin ? (Guid?)null : currentUser.UserId;

            var query = _keyRepository.Table.Where(key => key.Id == keyId);
            if (scopeUserId.HasValue)
            {
                query = query.Where(key => key.OwnerUserId == scopeUserId.Value);
            }

            var existing = query.FirstOrDefault()
                ?? throw new InvalidOperationException("api key not found");
            _keyRepository.Delete(existing);
            return ApiOpResult<DeleteApiKeyResponse>.Succeed(new DeleteApiKeyResponse(true));
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<DeleteApiKeyResponse>.Fail(404, exception.Message);
        }
    }

    private static AccessApiKeyDto MapToDto(AccessApiKey key, string ownerUsername)
    {
        return new AccessApiKeyDto(
            key.Id,
            key.OwnerUserId,
            ownerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            $"{key.KeyPrefix}...{key.KeySuffix}",
            key.Enabled,
            key.CreatedAt,
            key.UpdatedAt,
            key.LastUsedAt,
            key.KeyPlaintext);
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

    private static ApiOpResult<ApiKeyResponsePayload> ValidationFailure(string message)
    {
        return ApiOpResult<ApiKeyResponsePayload>.Fail(400, message);
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
