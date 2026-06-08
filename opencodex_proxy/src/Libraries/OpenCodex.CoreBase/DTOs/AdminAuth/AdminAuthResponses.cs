using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.AdminAuth;

public sealed class AdminSessionResponse
{
    public AdminSessionResponse(bool authenticated, AdminSessionUserResponse? user)
    {
        Authenticated = authenticated;
        User = user;
    }

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; }

    [JsonPropertyName("user")]
    public AdminSessionUserResponse? User { get; }

    public static AdminSessionResponse From(
        string username,
        string role,
        bool enabled)
    {
        return new AdminSessionResponse(
            true,
            new AdminSessionUserResponse(username, role, enabled));
    }

    public static AdminSessionResponse LoggedOut()
    {
        return new AdminSessionResponse(false, null);
    }
}

public sealed class AdminSessionUserResponse
{
    public AdminSessionUserResponse(string username, string role, bool enabled)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    [JsonPropertyName("username")]
    public string Username { get; }

    [JsonPropertyName("role")]
    public string Role { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }
}
