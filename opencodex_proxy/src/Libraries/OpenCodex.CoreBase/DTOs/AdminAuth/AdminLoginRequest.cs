namespace OpenCodex.CoreBase.DTOs.AdminAuth;

public sealed class AdminLoginRequest
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
