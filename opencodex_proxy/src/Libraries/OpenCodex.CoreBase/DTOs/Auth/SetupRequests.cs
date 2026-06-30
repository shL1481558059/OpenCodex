using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs.SystemSettings;

namespace OpenCodex.CoreBase.DTOs.Auth;

public sealed class SetupRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("system_settings")]
    public SystemSettingsUpdateRequest? SystemSettings { get; set; }
}
