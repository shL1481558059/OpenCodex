namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 表示代理服务所需的运行时设置。
/// </summary>
public sealed class OpenCodexRuntimeSettings
{
    public OpenCodexRuntimeSettings(
        string databaseProvider,
        string connectionString,
        string adminUsername,
        string adminPassword,
        int defaultTimeout,
        string? ocrCacheDir = null,
        string? redisConnection = null,
        string? redisPrefix = null,
        int cacheDefaultTtlSeconds = 300,
        string? modelCatalogSyncUrl = null)
    {
        DatabaseProvider = databaseProvider;
        ConnectionString = connectionString;
        AdminUsername = adminUsername;
        AdminPassword = adminPassword;
        DefaultTimeout = defaultTimeout;
        OcrCacheDir = string.IsNullOrWhiteSpace(ocrCacheDir) ? "ocr-cache" : ocrCacheDir.Trim();
        RedisConnection = (redisConnection ?? string.Empty).Trim();
        RedisPrefix = string.IsNullOrWhiteSpace(redisPrefix) ? "opencodex" : redisPrefix.Trim();
        CacheDefaultTtlSeconds = cacheDefaultTtlSeconds > 0 ? cacheDefaultTtlSeconds : 300;
        ModelCatalogSyncUrl = modelCatalogSyncUrl;
    }

    /// <summary>
    /// 获取数据库提供程序标识(sqlite / postgres)。
    /// </summary>
    public string DatabaseProvider { get; }

    /// <summary>
    /// 获取数据库连接字符串。
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// 获取配置的管理员用户名。
    /// </summary>
    public string AdminUsername { get; }

    /// <summary>
    /// 获取配置的管理员密码。
    /// </summary>
    public string AdminPassword { get; }

    /// <summary>
    /// 获取默认上游请求超时时间,单位为秒。
    /// </summary>
    public int DefaultTimeout { get; }

    /// <summary>
    /// 获取 OCR 缓存目录。
    /// </summary>
    public string OcrCacheDir { get; }

    /// <summary>
    /// 获取 Redis 连接字符串;为空表示禁用 Redis(L2),缓存降级为纯进程内 L1。
    /// </summary>
    public string RedisConnection { get; }

    /// <summary>
    /// 获取 Redis 全局键前缀,默认 <c>opencodex</c>。
    /// </summary>
    public string RedisPrefix { get; }

    /// <summary>
    /// 获取缓存默认过期时长,单位为秒,默认 300。
    /// </summary>
    public int CacheDefaultTtlSeconds { get; }

    /// <summary>
    /// 获取模型目录同步源地址;为空表示使用内置默认值。
    /// </summary>
    public string? ModelCatalogSyncUrl { get; }
}
