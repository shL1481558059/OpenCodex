using OpenCodex.ProtocolConversion;

namespace OpenCodex.Api.Infrastructure;

/// <summary>
/// 代理端点的请求体上限。
/// </summary>
public static class ProxyRequestLimits
{
    private const string LegacyConfigKey = "OpenCodex:MaxRequestBodyMb";
    private const string LegacyEnvironmentAlias = "OPENCODEX_MAX_REQUEST_BODY_MB";

    /// <summary>
    /// 解析生效的请求体上限（字节）。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>请求体上限字节数。</returns>
    public static long ResolveMaxRequestBodyBytes(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RejectLegacyKey(configuration, LegacyConfigKey);
        RejectLegacyKey(configuration, LegacyEnvironmentAlias);
        return ProtocolConversionParameters.Current.MaxRequestBodyBytes;
    }

    private static void RejectLegacyKey(IConfiguration configuration, string key)
    {
        if (configuration[key] is not null)
        {
            throw new InvalidOperationException(
                $"Deprecated request-capacity configuration key {key}. Configure ProtocolConversion__MaxRequestBodyBytes in .env instead.");
        }
    }
}
