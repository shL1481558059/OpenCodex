using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.SystemSettings;

public sealed class SystemSettingsUpdateRequest
{
    [JsonPropertyName("access_mode")]
    public string? AccessMode { get; set; }

    [JsonPropertyName("bind_host")]
    public string? BindHost { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }
}

public sealed class SystemSettingsResponse
{
    public SystemSettingsResponse(
        string accessMode,
        string bindHost,
        int port,
        bool managedByDesktop,
        bool restartRequired)
    {
        AccessMode = accessMode;
        BindHost = bindHost;
        Port = port;
        ManagedByDesktop = managedByDesktop;
        RestartRequired = restartRequired;
    }

    [JsonPropertyName("access_mode")]
    public string AccessMode { get; }

    [JsonPropertyName("bind_host")]
    public string BindHost { get; }

    [JsonPropertyName("port")]
    public int Port { get; }

    [JsonPropertyName("managed_by_desktop")]
    public bool ManagedByDesktop { get; }

    [JsonPropertyName("restart_required")]
    public bool RestartRequired { get; }

    [JsonPropertyName("admin_url")]
    public string AdminUrl => $"http://127.0.0.1:{Port}/admin/";
}
