namespace OpenCodex.Api.Domain;

public sealed class UserEntity : BaseEntity<string>
{
    public UserEntity(
        string username,
        string passwordHash,
        string role,
        bool enabled,
        double createdAt,
        double updatedAt)
    {
        Id = username;
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string Username { get; init; }

    public string PasswordHash { get; init; }

    public string Role { get; init; }

    public bool Enabled { get; init; }

    public double CreatedAt { get; init; }

    public double UpdatedAt { get; init; }

    public UserRecord ToRecord()
    {
        return new UserRecord(Username, Role, Enabled, CreatedAt, UpdatedAt);
    }

    public override object? GetId()
    {
        return Username.Length == 0 ? null : Username;
    }
}
