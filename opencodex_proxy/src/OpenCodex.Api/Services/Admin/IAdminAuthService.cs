using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminAuthenticatedUser
{
    public AdminAuthenticatedUser(string username, string role, bool enabled)
    {
        Username = username;
        Role = role;
        Enabled = enabled;
    }

    public string Username { get; }

    public string Role { get; }

    public bool Enabled { get; }
}

public interface IAdminAuthService
{
    ServiceResult<AdminAuthenticatedUser> Login(string? username, string? password);
}
