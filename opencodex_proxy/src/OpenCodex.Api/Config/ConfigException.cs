namespace OpenCodex.Api.Config;

public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }
}
