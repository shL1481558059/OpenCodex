using OpenCodex.ProtocolConversion;
namespace OpenCodex.Api.Hosting;

internal static class RuntimeCapacityConfiguration
{
    internal static void Validate(IConfiguration configuration)
    {
        _ = ProtocolConversionParameters.Current.MaxRequestBodyBytes;
        string[] deprecatedKeys =
        [
            "OpenCodex:MaxRequestBodyMb", "OPENCODEX_MAX_REQUEST_BODY_MB",
            "ProxyAuditSpool:MaxBodyBytes", "ProxyAuditSpool:MaxTotalBytes", "ProxyAuditSpool:MaxFiles"
        ];
        foreach (var key in deprecatedKeys)
            if (configuration[key] is not null)
                throw new InvalidOperationException(
                    $"Deprecated capacity setting '{key}'. Migrate capacity values to the .env keys with the ProtocolConversion__ prefix.");
        if (configuration.GetSection("MultiAgent:ClientWebSocketCache").Exists())
            throw new InvalidOperationException("Deprecated setting 'MultiAgent:ClientWebSocketCache'. Client history now uses the shared root snapshot budget.");
    }
}
