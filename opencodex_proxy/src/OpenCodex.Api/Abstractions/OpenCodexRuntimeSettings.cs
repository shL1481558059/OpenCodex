namespace OpenCodex.Api.Abstractions;

public sealed class OpenCodexRuntimeSettings
{
    public OpenCodexRuntimeSettings(
        string dbPath,
        string adminUsername,
        string adminPassword,
        int defaultTimeout)
    {
        DbPath = dbPath;
        AdminUsername = adminUsername;
        AdminPassword = adminPassword;
        DefaultTimeout = defaultTimeout;
    }

    public string DbPath { get; }

    public string AdminUsername { get; }

    public string AdminPassword { get; }

    public int DefaultTimeout { get; }
}
