namespace OpenCodex.Api.Configuration;

public sealed class OpenCodexSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string AdminPassword { get; init; }

    public required string DbPath { get; init; }

    public required string LogPath { get; init; }

    public required string LogLevel { get; init; }

    public required string LogViewLevel { get; init; }

    public required int DefaultTimeout { get; init; }

    public required string SecretKey { get; init; }

    public required string AdminUsername { get; init; }
}

public sealed class OpenCodexSettingsException : Exception
{
    public OpenCodexSettingsException(string message)
        : base(message)
    {
    }
}
