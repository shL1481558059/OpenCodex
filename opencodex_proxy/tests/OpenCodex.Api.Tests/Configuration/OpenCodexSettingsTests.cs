using Microsoft.Extensions.Configuration;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Tests.Configuration;

public sealed class OpenCodexSettingsTests
{
    [Fact]
    public void FromValuesUsesPythonDefaultsWhenOnlyAdminPasswordIsProvided()
    {
        var settings = OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
        {
            ["OPENCODEX_ADMIN_PASSWORD"] = "secret"
        });

        Assert.Equal("0.0.0.0", settings.Host);
        Assert.Equal(8000, settings.Port);
        Assert.Equal("secret", settings.AdminPassword);
        Assert.Equal("logs/opencodex.db", settings.DbPath);
        Assert.Equal("logs/opencodex.log", settings.LogPath);
        Assert.Equal("INFO", settings.LogLevel);
        Assert.Equal("BASIC", settings.LogViewLevel);
        Assert.Equal(120, settings.DefaultTimeout);
        Assert.Equal("change-me-session-secret", settings.SecretKey);
        Assert.Equal("admin", settings.AdminUsername);
        Assert.Equal(7.25, PricingDefaults.UsdCnyRate);
    }

    [Fact]
    public void FromValuesTrimsAndNormalizesPythonCompatibleFields()
    {
        var settings = OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
        {
            ["OPENCODEX_HOST"] = " 127.0.0.1 ",
            ["OPENCODEX_PORT"] = " 9000 ",
            ["OPENCODEX_ADMIN_USERNAME"] = " root ",
            ["OPENCODEX_ADMIN_PASSWORD"] = " secret ",
            ["OPENCODEX_DB_PATH"] = " data/opencodex.db ",
            ["OPENCODEX_LOG_PATH"] = " data/opencodex.log ",
            ["OPENCODEX_LOG_LEVEL"] = " debug ",
            ["OPENCODEX_LOG_VIEW_LEVEL"] = " trace ",
            ["OPENCODEX_DEFAULT_TIMEOUT"] = " 30 ",
            ["OPENCODEX_SECRET_KEY"] = " session-secret "
        });

        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal(9000, settings.Port);
        Assert.Equal("root", settings.AdminUsername);
        Assert.Equal("secret", settings.AdminPassword);
        Assert.Equal(" data/opencodex.db ", settings.DbPath);
        Assert.Equal(" data/opencodex.log ", settings.LogPath);
        Assert.Equal("DEBUG", settings.LogLevel);
        Assert.Equal("TRACE", settings.LogViewLevel);
        Assert.Equal(30, settings.DefaultTimeout);
        Assert.Equal(" session-secret ", settings.SecretKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromValuesRequiresAdminPassword(string? password)
    {
        var exception = Assert.Throws<OpenCodexSettingsException>(() =>
            OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_PASSWORD"] = password
            }));

        Assert.Equal("OPENCODEX_ADMIN_PASSWORD is required", exception.Message);
    }

    [Fact]
    public void FromValuesFallsBackForBlankHostAdminUsernameAndIntegers()
    {
        var settings = OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
        {
            ["OPENCODEX_HOST"] = " ",
            ["OPENCODEX_PORT"] = " ",
            ["OPENCODEX_ADMIN_USERNAME"] = " ",
            ["OPENCODEX_ADMIN_PASSWORD"] = "secret",
            ["OPENCODEX_DEFAULT_TIMEOUT"] = ""
        });

        Assert.Equal("0.0.0.0", settings.Host);
        Assert.Equal(8000, settings.Port);
        Assert.Equal("admin", settings.AdminUsername);
        Assert.Equal(120, settings.DefaultTimeout);
    }

    [Theory]
    [InlineData("OPENCODEX_PORT")]
    [InlineData("OPENCODEX_DEFAULT_TIMEOUT")]
    public void FromValuesRejectsInvalidIntegers(string name)
    {
        var exception = Assert.Throws<OpenCodexSettingsException>(() =>
            OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_PASSWORD"] = "secret",
                [name] = "not-an-int"
            }));

        Assert.Equal($"{name} must be an integer", exception.Message);
    }

    [Theory]
    [InlineData("OPENCODEX_PORT", "0")]
    [InlineData("OPENCODEX_PORT", "-1")]
    [InlineData("OPENCODEX_DEFAULT_TIMEOUT", "0")]
    [InlineData("OPENCODEX_DEFAULT_TIMEOUT", "-1")]
    public void FromValuesRequiresPositiveIntegers(string name, string value)
    {
        var exception = Assert.Throws<OpenCodexSettingsException>(() =>
            OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_PASSWORD"] = "secret",
                [name] = value
            }));

        Assert.Equal($"{name} must be greater than zero", exception.Message);
    }

    [Fact]
    public void FromValuesRejectsUnknownLogLevel()
    {
        var exception = Assert.Throws<OpenCodexSettingsException>(() =>
            OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_PASSWORD"] = "secret",
                ["OPENCODEX_LOG_LEVEL"] = "verbose"
            }));

        Assert.Equal(
            "OPENCODEX_LOG_LEVEL must be one of ['CRITICAL', 'DEBUG', 'ERROR', 'INFO', 'WARNING']",
            exception.Message);
    }

    [Fact]
    public void FromValuesRejectsUnknownLogViewLevel()
    {
        var exception = Assert.Throws<OpenCodexSettingsException>(() =>
            OpenCodexSettingsLoader.FromValues(new Dictionary<string, string?>
            {
                ["OPENCODEX_ADMIN_PASSWORD"] = "secret",
                ["OPENCODEX_LOG_VIEW_LEVEL"] = "full"
            }));

        Assert.Equal(
            "OPENCODEX_LOG_VIEW_LEVEL must be one of ['BASIC', 'DEBUG', 'TRACE']",
            exception.Message);
    }

    [Fact]
    public void FromEnvironmentLoadsDotEnvAndEnvironmentVariablesOverrideDotEnv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"opencodex-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dotenvPath = Path.Combine(tempDir, ".env");
        var environmentBackup = BackupEnvironment();

        try
        {
            ClearOpenCodexEnvironment();
            File.WriteAllLines(dotenvPath,
            [
                "OPENCODEX_ADMIN_PASSWORD=dotenv-secret",
                "OPENCODEX_PORT=7000",
                "OPENCODEX_LOG_LEVEL=warning"
            ]);

            Environment.SetEnvironmentVariable("OPENCODEX_ADMIN_PASSWORD", "env-secret");

            var settings = OpenCodexSettingsLoader.FromEnvironment(dotenvPath);

            Assert.Equal("env-secret", settings.AdminPassword);
            Assert.Equal(7000, settings.Port);
            Assert.Equal("WARNING", settings.LogLevel);
        }
        finally
        {
            RestoreEnvironment(environmentBackup);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DotEnvDefaultsLoadsOnlyMissingConfigurationValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"opencodex-dotenv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dotenvPath = Path.Combine(tempDir, ".env");

        try
        {
            File.WriteAllLines(dotenvPath,
            [
                "# ignored comment",
                "export OPENCODEX_ADMIN_PASSWORD=dotenv-secret",
                "OPENCODEX_PORT=7000",
                "OPENCODEX_SECRET_KEY=\"quoted-secret\"",
                "ignored-line"
            ]);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OPENCODEX_ADMIN_PASSWORD"] = "configured-secret"
                })
                .Build();

            var defaults = DotEnvDefaults.Load(dotenvPath, configuration);

            Assert.False(defaults.ContainsKey("OPENCODEX_ADMIN_PASSWORD"));
            Assert.Equal("7000", defaults["OPENCODEX_PORT"]);
            Assert.Equal("quoted-secret", defaults["OPENCODEX_SECRET_KEY"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RuntimeSettingsProviderUsesOpenCodexSectionBeforeEnvironmentFallbacks()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODEX_DB_PATH"] = "env.db",
                ["OPENCODEX_ADMIN_USERNAME"] = "env-admin",
                ["OPENCODEX_ADMIN_PASSWORD"] = "env-pw",
                ["OPENCODEX_DEFAULT_TIMEOUT"] = "30",
                ["OpenCodex:DbPath"] = "section.db",
                ["OpenCodex:AdminUsername"] = " section-admin ",
                ["OpenCodex:AdminPassword"] = " section-pw ",
                ["OpenCodex:DefaultTimeout"] = "45"
            })
            .Build();

        var settings = new OpenCodexRuntimeSettingsProvider(configuration).GetSettings();

        Assert.Equal("section.db", settings.DbPath);
        Assert.Equal("section-admin", settings.AdminUsername);
        Assert.Equal("section-pw", settings.AdminPassword);
        Assert.Equal(45, settings.DefaultTimeout);
    }

    [Fact]
    public void RuntimeSettingsProviderUsesPythonCompatibleDefaultsForMissingOrBlankValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenCodex:AdminUsername"] = " ",
                ["OpenCodex:DefaultTimeout"] = "0"
            })
            .Build();

        var settings = new OpenCodexRuntimeSettingsProvider(configuration).GetSettings();

        Assert.Equal("logs/opencodex.db", settings.DbPath);
        Assert.Equal("admin", settings.AdminUsername);
        Assert.Equal(string.Empty, settings.AdminPassword);
        Assert.Equal(120, settings.DefaultTimeout);
    }

    private static Dictionary<string, string?> BackupEnvironment()
    {
        return EnvironmentKeys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
    }

    private static void ClearOpenCodexEnvironment()
    {
        foreach (var key in EnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    private static void RestoreEnvironment(Dictionary<string, string?> backup)
    {
        foreach (var (key, value) in backup)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static readonly string[] EnvironmentKeys =
    [
        "OPENCODEX_HOST",
        "OPENCODEX_PORT",
        "OPENCODEX_ADMIN_USERNAME",
        "OPENCODEX_ADMIN_PASSWORD",
        "OPENCODEX_DB_PATH",
        "OPENCODEX_LOG_PATH",
        "OPENCODEX_LOG_LEVEL",
        "OPENCODEX_LOG_VIEW_LEVEL",
        "OPENCODEX_DEFAULT_TIMEOUT",
        "OPENCODEX_SECRET_KEY"
    ];
}
