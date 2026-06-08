namespace OpenCodex.CoreBase.Services.Admin;

public sealed class AdminSessionUser
{
    public AdminSessionUser(string username, string role, bool enabled)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    public string Username { get; }

    public string Role { get; }

    public bool Enabled { get; }
}
