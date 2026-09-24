namespace OpenCodex.Api.Infrastructure;

/// <summary>
/// 代理端点的请求体上限。
/// </summary>
public static class ProxyRequestLimits
{
    /// <summary>
    /// 未配置时使用的默认上限（MiB）。
    /// </summary>
    public const int DefaultMaxRequestBodyMegabytes = 50;

    private const int MinimumMegabytes = 1;
    private const int MaximumMegabytes = 512;
    private const string ConfigKey = "OpenCodex:MaxRequestBodyMb";
    private const string EnvironmentKey = "OPENCODEX_MAX_REQUEST_BODY_MB";

    /// <summary>
    /// 解析生效的请求体上限（字节）。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>请求体上限字节数。</returns>
    public static long ResolveMaxRequestBodyBytes(IConfiguration configuration)
    {
        var raw = configuration[ConfigKey] ?? configuration[EnvironmentKey];
        var megabytes = int.TryParse(raw, out var parsed)
            ? Math.Clamp(parsed, MinimumMegabytes, MaximumMegabytes)
            : DefaultMaxRequestBodyMegabytes;
        return megabytes * 1024L * 1024L;
    }
}
