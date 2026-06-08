namespace OpenCodex.Core.Domain;

public sealed class User : BaseEntity<string>
{
    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string Role { get; set; } = "user";

    public bool Enabled { get; set; } = true;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }

    public List<Channel> Channels { get; set; } = [];

    public List<AccessApiKey> AccessApiKeys { get; set; } = [];

    public override object? GetId()
    {
        return Username.Length == 0 ? null : Username;
    }
}
