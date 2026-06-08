using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.CoreBase.DTOs.AdminUsers;

public sealed class AdminUserCreateRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    public AdminUserCreateCommand ToCommand()
    {
        return new AdminUserCreateCommand(
            Username,
            Password,
            Enabled is not false);
    }
}

public sealed class AdminUserUpdateRequest
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    public AdminUserUpdateCommand ToCommand()
    {
        return new AdminUserUpdateCommand(Enabled, Password);
    }
}
