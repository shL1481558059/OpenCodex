using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodex.CoreBase.DTOs.SystemSettings;

namespace OpenCodex.Api.Configuration;

public sealed class DesktopSystemSettingsStore : IDesktopSystemSettingsStore
{
    private const string LocalhostMode = "localhost";
    private const string LanMode = "lan";
    private const string LocalhostBindHost = "127.0.0.1";
    private const string LanBindHost = "0.0.0.0";
    private const int DefaultPort = 18080;
    private const int MinPort = 1024;
    private const int MaxPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;

    public DesktopSystemSettingsStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SystemSettingsResponse Get()
    {
        return ToResponse(Load(), restartRequired: false);
    }

    public DesktopSystemSettingsDraft Normalize(SystemSettingsUpdateRequest? request)
    {
        var current = Load();
        var accessMode = NormalizeAccessMode(request?.AccessMode, request?.BindHost, current.AccessMode);
        return new DesktopSystemSettingsDraft(
            accessMode,
            BindHostFromAccessMode(accessMode),
            NormalizePort(request?.Port, current.Port));
    }

    public SystemSettingsResponse Save(DesktopSystemSettingsDraft draft)
    {
        var current = Load();
        var settings = new DesktopSystemSettingsFile
        {
            AccessMode = draft.AccessMode,
            BindHost = draft.BindHost,
            Port = draft.Port
        };

        var path = SettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        return ToResponse(
            settings,
            restartRequired: current.AccessMode != settings.AccessMode ||
                current.BindHost != settings.BindHost ||
                current.Port != settings.Port);
    }

    private DesktopSystemSettingsFile Load()
    {
        var path = SettingsPath();
        if (File.Exists(path))
        {
            var settings = JsonSerializer.Deserialize<DesktopSystemSettingsFile>(
                File.ReadAllText(path),
                JsonOptions);
            if (settings is not null)
            {
                var accessMode = NormalizeAccessMode(settings.AccessMode, settings.BindHost, DefaultAccessMode());
                return new DesktopSystemSettingsFile
                {
                    AccessMode = accessMode,
                    BindHost = BindHostFromAccessMode(accessMode),
                    Port = NormalizePort(settings.Port, DefaultRuntimePort())
                };
            }
        }

        var defaultAccessMode = DefaultAccessMode();
        return new DesktopSystemSettingsFile
        {
            AccessMode = defaultAccessMode,
            BindHost = BindHostFromAccessMode(defaultAccessMode),
            Port = DefaultRuntimePort()
        };
    }

    private SystemSettingsResponse ToResponse(
        DesktopSystemSettingsFile settings,
        bool restartRequired)
    {
        return new SystemSettingsResponse(
            settings.AccessMode,
            settings.BindHost,
            settings.Port,
            ManagedByDesktop(),
            restartRequired);
    }

    private string SettingsPath()
    {
        var configured = _configuration["OPENCODEX_DESKTOP_SETTINGS_PATH"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath("desktop-settings.json")
            : Path.GetFullPath(configured.Trim());
    }

    private bool ManagedByDesktop()
    {
        return !string.IsNullOrWhiteSpace(_configuration["OPENCODEX_DESKTOP_SETTINGS_PATH"]);
    }

    private string DefaultAccessMode()
    {
        var configuredHost = (_configuration["OPENCODEX_DESKTOP_BIND_HOST"] ?? string.Empty).Trim();
        if (configuredHost == LanBindHost)
        {
            return LanMode;
        }

        if (configuredHost == LocalhostBindHost || configuredHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return LocalhostMode;
        }

        var urls = _configuration["ASPNETCORE_URLS"] ?? string.Empty;
        return urls.Contains($"://{LanBindHost}:", StringComparison.Ordinal)
            ? LanMode
            : LocalhostMode;
    }

    private int DefaultRuntimePort()
    {
        var configuredPort = _configuration["OPENCODEX_DESKTOP_PORT"];
        if (int.TryParse(configuredPort, out var port) && PortInRange(port))
        {
            return port;
        }

        return TryReadPortFromUrls(_configuration["ASPNETCORE_URLS"]) ?? DefaultPort;
    }

    private static int? TryReadPortFromUrls(string? urls)
    {
        foreach (var rawUrl in (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && PortInRange(uri.Port))
            {
                return uri.Port;
            }
        }

        return null;
    }

    private static string NormalizeAccessMode(string? accessMode, string? bindHost, string fallback)
    {
        var normalizedMode = (accessMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode is LocalhostMode or "local")
        {
            return LocalhostMode;
        }

        if (normalizedMode is LanMode or "network")
        {
            return LanMode;
        }

        var normalizedHost = (bindHost ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedHost is LocalhostBindHost or "localhost")
        {
            return LocalhostMode;
        }

        if (normalizedHost == LanBindHost)
        {
            return LanMode;
        }

        if (!string.IsNullOrWhiteSpace(accessMode) || !string.IsNullOrWhiteSpace(bindHost))
        {
            throw new ArgumentException("access_mode must be localhost or lan");
        }

        return fallback == LanMode ? LanMode : LocalhostMode;
    }

    private static string BindHostFromAccessMode(string accessMode)
    {
        return accessMode == LanMode ? LanBindHost : LocalhostBindHost;
    }

    private static int NormalizePort(int? requestedPort, int fallback)
    {
        var port = requestedPort ?? fallback;
        if (!PortInRange(port))
        {
            throw new ArgumentException($"port must be between {MinPort} and {MaxPort}");
        }

        return port;
    }

    private static bool PortInRange(int port)
    {
        return port is >= MinPort and <= MaxPort;
    }

    private sealed class DesktopSystemSettingsFile
    {
        [JsonPropertyName("access_mode")]
        public string AccessMode { get; set; } = LocalhostMode;

        [JsonPropertyName("bind_host")]
        public string BindHost { get; set; } = LocalhostBindHost;

        [JsonPropertyName("port")]
        public int Port { get; set; } = DefaultPort;
    }
}
