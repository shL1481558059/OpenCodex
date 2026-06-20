namespace OpenCodex.Api.Configuration;

public sealed class OpenCodexSettings
{
    public required string AdminPassword { get; init; }

    public required string DatabaseProvider { get; init; }

    public required string ConnectionString { get; init; }

    public required string LogPath { get; init; }

    public required string LogLevel { get; init; }

    public required string LogViewLevel { get; init; }

    public required int DefaultTimeout { get; init; }

    public required int AdminCookieDays { get; init; }

    public required string SecretKey { get; init; }

    public required string DataProtectionKeysPath { get; init; }

    public required string AdminUsername { get; init; }

    public required string OcrCacheDir { get; init; }

    public required string LocalOcrModel { get; init; }
}

public sealed class OpenCodexSettingsException : Exception
{
    public OpenCodexSettingsException(string message)
        : base(message)
    {
    }
}
