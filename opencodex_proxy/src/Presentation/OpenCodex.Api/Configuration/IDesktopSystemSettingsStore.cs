using OpenCodex.CoreBase.DTOs.SystemSettings;

namespace OpenCodex.Api.Configuration;

public interface IDesktopSystemSettingsStore
{
    SystemSettingsResponse Get();

    DesktopSystemSettingsDraft Normalize(SystemSettingsUpdateRequest? request);

    SystemSettingsResponse Save(DesktopSystemSettingsDraft draft);
}

public sealed class DesktopSystemSettingsDraft
{
    public DesktopSystemSettingsDraft(string accessMode, string bindHost, int port)
    {
        AccessMode = accessMode;
        BindHost = bindHost;
        Port = port;
    }

    public string AccessMode { get; }

    public string BindHost { get; }

    public int Port { get; }
}
