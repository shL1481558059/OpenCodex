using System.Text.Json.Serialization;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.DTOs.AdminUsers;

public sealed class UsersResponse
{
    public UsersResponse(IReadOnlyList<UserResponse> users)
    {
        Users = users;
    }

    [JsonPropertyName("users")]
    public IReadOnlyList<UserResponse> Users { get; }

    public static UsersResponse From(IReadOnlyList<UserRecord>? users)
    {
        return new UsersResponse(users?.Select(UserResponse.From).ToList() ?? []);
    }
}

public sealed class UserResponsePayload
{
    public UserResponsePayload(UserResponse user)
    {
        User = user;
    }

    [JsonPropertyName("user")]
    public UserResponse User { get; }

    public static UserResponsePayload From(UserRecord user)
    {
        return new UserResponsePayload(UserResponse.From(user));
    }
}

public sealed class DeleteUserResponse
{
    public DeleteUserResponse(bool deleted, UserResponse user)
    {
        Deleted = deleted;
        User = user;
    }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; }

    [JsonPropertyName("user")]
    public UserResponse User { get; }

    public static DeleteUserResponse From(UserRecord user)
    {
        return new DeleteUserResponse(true, UserResponse.From(user));
    }
}

public sealed class UserResponse
{
    public UserResponse(
        string username,
        string role,
        bool enabled,
        double createdAt,
        double updatedAt)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    [JsonPropertyName("username")]
    public string Username { get; }

    [JsonPropertyName("role")]
    public string Role { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }

    public static UserResponse From(UserRecord user)
    {
        return new UserResponse(
            user.Username,
            user.Role,
            user.Enabled,
            user.CreatedAt,
            user.UpdatedAt);
    }
}
