using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Configuration;

public sealed class OpenCodexRuntimeSettingsProvider : IOpenCodexRuntimeSettingsProvider
{
    private readonly IConfiguration _configuration;

    public OpenCodexRuntimeSettingsProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public OpenCodexRuntimeSettings GetSettings()
    {
        return new OpenCodexRuntimeSettings(
            ConfigValue("OpenCodex:DbPath", "OPENCODEX_DB_PATH") ?? "logs/opencodex.db",
            AdminUsername(),
            (ConfigValue("OpenCodex:AdminPassword", "OPENCODEX_ADMIN_PASSWORD") ?? string.Empty).Trim(),
            PositiveInt("OpenCodex:DefaultTimeout", "OPENCODEX_DEFAULT_TIMEOUT", 120));
    }

    private string AdminUsername()
    {
        var username = (ConfigValue("OpenCodex:AdminUsername", "OPENCODEX_ADMIN_USERNAME") ?? "admin").Trim();
        return username.Length == 0 ? "admin" : username;
    }

    private int PositiveInt(string primaryKey, string fallbackKey, int defaultValue)
    {
        var value = ConfigValue(primaryKey, fallbackKey);
        return int.TryParse(value, out var parsedValue) && parsedValue > 0
            ? parsedValue
            : defaultValue;
    }

    private string? ConfigValue(string primaryKey, string fallbackKey)
    {
        return _configuration[primaryKey] ?? _configuration[fallbackKey];
    }
}
