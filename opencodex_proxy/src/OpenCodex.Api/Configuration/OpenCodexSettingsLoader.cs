using System.Collections;
using System.Globalization;

namespace OpenCodex.Api.Configuration;

public static class OpenCodexSettingsLoader
{
    private static readonly string[] LogLevels =
    [
        "CRITICAL",
        "DEBUG",
        "ERROR",
        "INFO",
        "WARNING"
    ];

    private static readonly string[] LogViewLevels =
    [
        "BASIC",
        "DEBUG",
        "TRACE"
    ];

    public static OpenCodexSettings FromEnvironment(string? dotenvPath = null)
    {
        var values = LoadDotEnvFile(dotenvPath ?? ".env");

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value?.ToString();
            }
        }

        return FromValues(values);
    }

    public static OpenCodexSettings FromValues(IReadOnlyDictionary<string, string?> values)
    {
        var adminPassword = (GetValue(values, "OPENCODEX_ADMIN_PASSWORD") ?? string.Empty).Trim();
        if (adminPassword.Length == 0)
        {
            throw new OpenCodexSettingsException("OPENCODEX_ADMIN_PASSWORD is required");
        }

        var adminUsername = (GetValue(values, "OPENCODEX_ADMIN_USERNAME") ?? "admin").Trim();
        if (adminUsername.Length == 0)
        {
            adminUsername = "admin";
        }

        var logLevel = (GetValue(values, "OPENCODEX_LOG_LEVEL") ?? "INFO").Trim().ToUpperInvariant();
        if (!LogLevels.Contains(logLevel, StringComparer.Ordinal))
        {
            throw new OpenCodexSettingsException(
                $"OPENCODEX_LOG_LEVEL must be one of {PythonList(LogLevels)}");
        }

        var logViewLevel = (GetValue(values, "OPENCODEX_LOG_VIEW_LEVEL") ?? "BASIC").Trim().ToUpperInvariant();
        if (!LogViewLevels.Contains(logViewLevel, StringComparer.Ordinal))
        {
            throw new OpenCodexSettingsException(
                $"OPENCODEX_LOG_VIEW_LEVEL must be one of {PythonList(LogViewLevels)}");
        }

        var host = (GetValue(values, "OPENCODEX_HOST") ?? "0.0.0.0").Trim();
        if (host.Length == 0)
        {
            host = "0.0.0.0";
        }

        return new OpenCodexSettings
        {
            Host = host,
            Port = ParsePositiveInt(values, "OPENCODEX_PORT", 8000),
            AdminPassword = adminPassword,
            DbPath = GetValue(values, "OPENCODEX_DB_PATH") ?? "logs/opencodex.db",
            LogPath = GetValue(values, "OPENCODEX_LOG_PATH") ?? "logs/opencodex.log",
            LogLevel = logLevel,
            LogViewLevel = logViewLevel,
            DefaultTimeout = ParsePositiveInt(values, "OPENCODEX_DEFAULT_TIMEOUT", 120),
            SecretKey = GetValue(values, "OPENCODEX_SECRET_KEY") ?? "change-me-session-secret",
            AdminUsername = adminUsername
        };
    }

    private static Dictionary<string, string?> LoadDotEnvFile(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed["export ".Length..].TrimStart();
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = Unquote(trimmed[(separatorIndex + 1)..].Trim());
        }

        return values;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string?> values, string name)
    {
        return values.TryGetValue(name, out var value) ? value : null;
    }

    private static int ParsePositiveInt(
        IReadOnlyDictionary<string, string?> values,
        string name,
        int defaultValue)
    {
        var raw = GetValue(values, name);
        if (raw is null || raw.Trim().Length == 0)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new OpenCodexSettingsException($"{name} must be an integer");
        }

        if (value <= 0)
        {
            throw new OpenCodexSettingsException($"{name} must be greater than zero");
        }

        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value[1..^1];
            }
        }

        return value;
    }

    private static string PythonList(IEnumerable<string> values)
    {
        return $"[{string.Join(", ", values.Select(value => $"'{value}'"))}]";
    }
}
