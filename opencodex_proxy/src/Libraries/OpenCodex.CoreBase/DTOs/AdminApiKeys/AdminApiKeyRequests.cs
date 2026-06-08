using System.Text.Json.Serialization;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.CoreBase.DTOs.AdminApiKeys;

public sealed class AdminApiKeyCreateRequest
{
    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    public AdminApiKeyCreateCommand ToCommand()
    {
        return new AdminApiKeyCreateCommand(OwnerUsername, Name);
    }
}

public sealed class AdminApiKeyUpdateRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    public AdminApiKeyUpdateCommand ToCommand()
    {
        return new AdminApiKeyUpdateCommand(Enabled);
    }
}
