using Mapster;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
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

    public UserService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWorkContext workContext,
        IRepository<User> userRepository,
        IRepository<AccessApiKey> apiKeyRepository,
        IRepository<Channel> channelRepository)
    {
        _settingsProvider = settingsProvider;
        _workContext = workContext;
        _userRepository = userRepository;
        _apiKeyRepository = apiKeyRepository;
        _channelRepository = channelRepository;
    }

    public ApiOpResult<UsersResponse> ListUsers()
    {
        var users = _userRepository.TableNoTracking
            .OrderBy(user => user.Role)
            .ThenBy(user => user.Username)
            .Select(user => user.Adapt<UserDto>())
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

    public ApiOpResult<UserResponsePayload> UpdateUser(string username, UserUpdateCommand command)
    {
        try
        {
            var settings = _settingsProvider.GetSettings();
            UserDto user;
            if (command.Enabled.HasValue)
            {
                user = SetUserEnabled(username, command.Enabled.Value);
            }
            else
            {
                user = GetUser(username) ?? throw new InvalidOperationException("user not found");
            }

            if (command.Password is not null)
            {
                if (string.Equals(username, settings.AdminUsername, StringComparison.Ordinal))
                {
                    return ValidationFailure("environment superadmin password is managed by env");
                }

                user = ResetUserPassword(username, command.Password.Trim());
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

    public ApiOpResult<DeleteUserResponse> DeleteUser(string username)
    {
        try
        {
            var currentUser = _workContext.RequireUser();
            var user = DeleteUser(username, currentUser.Username);
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
        return created.Adapt<UserDto>();
    }

    private UserDto? GetUser(string username)
    {
        username = NormalizeUsername(username);
        if (username.Length == 0)
        {
            return null;
        }

        var user = _userRepository.TableNoTracking.FirstOrDefault(item => item.Username == username);
        return user is null ? null : user.Adapt<UserDto>();
    }

    private UserDto SetUserEnabled(string username, bool enabled)
    {
        username = NormalizeUsername(username);
        var protectedUsername = NormalizeUsername(_settingsProvider.GetSettings().AdminUsername);
        if (username.Length == 0)
        {
            throw new ArgumentException("username is required", nameof(username));
        }

        if (protectedUsername.Length > 0 && username == protectedUsername && !enabled)
        {
            throw new InvalidOperationException("cannot disable the environment superadmin");
        }

        var user = _userRepository.Table.FirstOrDefault(item => item.Username == username)
            ?? throw new InvalidOperationException("user not found");
        user.Enabled = enabled;
        user.UpdatedAt = UnixTimeSeconds();
        _userRepository.Update(user);
        return user.Adapt<UserDto>();
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
        return user.Adapt<UserDto>();
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
        var deleted = user.Adapt<UserDto>();

        _apiKeyRepository.Delete(_apiKeyRepository.Table.Where(key => key.OwnerUserId == user.Id).ToList());
        _channelRepository.Delete(_channelRepository.Table.Where(channel => channel.OwnerUserId == user.Id).ToList());
        _userRepository.Delete(user);
        return deleted;
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
