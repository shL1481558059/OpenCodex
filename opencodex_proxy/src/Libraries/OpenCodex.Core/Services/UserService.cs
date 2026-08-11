using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Mapping;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Users;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class UserService : IUserService
{
    private static readonly HashSet<string> UserRoles = new(StringComparer.Ordinal)
    {
        "superadmin",
        "user"
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWorkContext _workContext;
    private readonly IRepository<User> _userRepository;
    private readonly IRepository<AccessApiKey> _apiKeyRepository;
    private readonly IRepository<Channel> _channelRepository;
    private readonly ICacheService _cache;

    public UserService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWorkContext workContext,
        IRepository<User> userRepository,
        IRepository<AccessApiKey> apiKeyRepository,
        IRepository<Channel> channelRepository,
        ICacheService cache)
    {
        _settingsProvider = settingsProvider;
        _workContext = workContext;
        _userRepository = userRepository;
        _apiKeyRepository = apiKeyRepository;
        _channelRepository = channelRepository;
        _cache = cache;
    }

    public ApiOpResult<UsersResponse> ListUsers()
    {
        var users = _userRepository.TableNoTracking
            .OrderBy(user => user.Role)
            .ThenBy(user => user.Username)
            .Select(user => user.ToDto())
            .ToList();
        return ApiOpResult<UsersResponse>.Succeed(UsersResponse.From(users));
    }

    public ApiOpResult<UserResponsePayload> CreateUser(UserCreateCommand command)
    {
        try
        {
            var user = CreateUser(command.Username.Trim(), command.Password.Trim(), enabled: command.Enabled);
            return ApiOpResult<UserResponsePayload>.Succeed(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public async Task<ApiOpResult<UserResponsePayload>> UpdateUserAsync(string username, UserUpdateCommand command)
    {
        try
        {
            var settings = _settingsProvider.GetSettings();
            UserDto user;
            if (command.Enabled.HasValue)
            {
                user = SetUserEnabled(username, command.Enabled.Value);
                // 启用状态变更影响 apikey 鉴权,失效该用户的鉴权快照。
                await _cache.RemoveAsync(CacheKeys.AuthUser(user.Id));
            }
            else
            {
                user = GetUser(username) ?? throw new InvalidOperationException("user not found");
            }

            if (command.Password is not null)
            {
                if (HasEnvironmentSuperadmin(settings) &&
                    string.Equals(username, settings.AdminUsername, StringComparison.Ordinal))
                {
                    return ValidationFailure("environment superadmin password is managed by env");
                }

                user = ResetUserPassword(username, command.Password.Trim());
                // 密码仅用于后台登录,不影响 apikey 鉴权,无需失效 auth 缓存。
            }

            return ApiOpResult<UserResponsePayload>.Succeed(UserResponsePayload.From(user));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiOpResult<UserResponsePayload>.Fail(404, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public async Task<ApiOpResult<DeleteUserResponse>> DeleteUserAsync(string username)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var user = DeleteUser(username, currentUser.Username);
            // 用户被删,其所有 apikey 也已级联删除;失效该用户的鉴权快照即可。
            // 残留的 apikey 快照因 user 快照缺失会在下次鉴权时判定失败,无需单独失效,
            // 且 apikey 快照会在 60s TTL 后自然过期。
            await _cache.RemoveAsync(CacheKeys.AuthUser(user.Id));
            return ApiOpResult<DeleteUserResponse>.Succeed(DeleteUserResponse.From(user));
        }
        catch (ArgumentException exception)
        {
            return ApiOpResult<DeleteUserResponse>.Fail(400, exception.Message);
        }
        catch (InvalidOperationException exception) when (exception.Message == "user not found")
        {
            return ApiOpResult<DeleteUserResponse>.Fail(404, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ApiOpResult<DeleteUserResponse>.Fail(400, exception.Message);
        }
    }

    private static ApiOpResult<UserResponsePayload> ValidationFailure(string message)
    {
        return ApiOpResult<UserResponsePayload>.Fail(400, message);
    }

    private UserDto CreateUser(string username, string password, string role = "user", bool enabled = true)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (!UserRoles.Contains(role))
        {
            throw new ArgumentException("role is invalid", nameof(role));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        if (_userRepository.TableNoTracking.Any(user => user.Username == username))
        {
            throw new ArgumentException("username already exists", nameof(username));
        }

        var now = UnixTimeSeconds();
        var created = new User
        {
            Username = username,
            PasswordHash = OpenCodexSecurity.HashPassword(password),
            Role = role,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        _userRepository.Insert(created);
        return created.ToDto();
    }

    private UserDto? GetUser(string username)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        var user = _userRepository.TableNoTracking.FirstOrDefault(item => item.Username == username);
        return user is null ? null : user.ToDto();
    }

    private UserDto SetUserEnabled(string username, bool enabled)
    {
        username = NormalizeUsername(username);
        var protectedUsername = NormalizeUsername(_settingsProvider.GetSettings().AdminUsername);
        var environmentSuperadminConfigured = HasEnvironmentSuperadmin(_settingsProvider.GetSettings());
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (environmentSuperadminConfigured && protectedUsername.Length > 0 && username == protectedUsername && !enabled)
        {
            throw new InvalidOperationException("cannot disable the environment superadmin");
        }

        var user = _userRepository.Table.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        user.Enabled = enabled;
        user.UpdatedAt = UnixTimeSeconds();
        _userRepository.Update(user);
        return user.ToDto();
    }

    private UserDto ResetUserPassword(string username, string password)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is required", nameof(password));
        }

        var user = _userRepository.Table.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        user.PasswordHash = OpenCodexSecurity.HashPassword(password);
        user.UpdatedAt = UnixTimeSeconds();
        _userRepository.Update(user);
        return user.ToDto();
    }

    private UserDto DeleteUser(string username, string protectedUsername)
    {
        username = NormalizeUsername(username);
        protectedUsername = NormalizeUsername(protectedUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length == 0)
        {
            throw new ArgumentException("protected_username is required", nameof(protectedUsername));
        }

        if (username == protectedUsername)
        {
            throw new InvalidOperationException("cannot delete current user");
        }

        var user = _userRepository.Table.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        var deleted = user.ToDto();

        _apiKeyRepository.Delete(_apiKeyRepository.Table.Where(key => key.OwnerUserId == user.Id).ToList());
        _channelRepository.Delete(_channelRepository.Table.Where(channel => channel.OwnerUserId == user.Id).ToList());
        _userRepository.Delete(user);
        return deleted;
    }

    private static string NormalizeUsername(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static bool HasEnvironmentSuperadmin(OpenCodexRuntimeSettings settings)
    {
        return settings.AdminPassword.Length > 0;
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
