using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs.SystemSettings;

namespace OpenCodex.CoreBase.DTOs.Auth;

public sealed class SetupStateResponse
{
    public SetupStateResponse(
        bool setupRequired,
        bool hasUsers,
        bool environmentSuperadminConfigured)
    {
        SetupRequired = setupRequired;
        HasUsers = hasUsers;
        EnvironmentSuperadminConfigured = environmentSuperadminConfigured;
    }

    [JsonPropertyName("setup_required")]
    public bool SetupRequired { get; }

    [JsonPropertyName("has_users")]
    public bool HasUsers { get; }

    [JsonPropertyName("environment_superadmin_configured")]
    public bool EnvironmentSuperadminConfigured { get; }
}

public sealed class SetupStatusResponse
{
    public SetupStatusResponse(
        bool setupRequired,
        bool hasUsers,
        bool environmentSuperadminConfigured,
        SystemSettingsResponse systemSettings)
    {
        SetupRequired = setupRequired;
        HasUsers = hasUsers;
        EnvironmentSuperadminConfigured = environmentSuperadminConfigured;
        SystemSettings = systemSettings;
    }

    [JsonPropertyName("setup_required")]
    public bool SetupRequired { get; }

    [JsonPropertyName("has_users")]
    public bool HasUsers { get; }

    [JsonPropertyName("environment_superadmin_configured")]
    public bool EnvironmentSuperadminConfigured { get; }

    [JsonPropertyName("system_settings")]
    public SystemSettingsResponse SystemSettings { get; }

    public static SetupStatusResponse From(
        SetupStateResponse state,
        SystemSettingsResponse systemSettings)
    {
        return new SetupStatusResponse(
            state.SetupRequired,
            state.HasUsers,
            state.EnvironmentSuperadminConfigured,
            systemSettings);
    }
}

public sealed class SetupCompleteResponse
{
    public SetupCompleteResponse(
        SessionResponse session,
        SystemSettingsResponse systemSettings)
    {
        Session = session;
        SystemSettings = systemSettings;
    }

    [JsonPropertyName("session")]
    public SessionResponse Session { get; }

    [JsonPropertyName("system_settings")]
    public SystemSettingsResponse SystemSettings { get; }
}
