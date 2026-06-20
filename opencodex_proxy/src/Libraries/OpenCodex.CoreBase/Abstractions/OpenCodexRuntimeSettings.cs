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
        string? localOcrModel = null)
    {
        DatabaseProvider = databaseProvider;
        ConnectionString = connectionString;
        AdminUsername = adminUsername;
        AdminPassword = adminPassword;
        DefaultTimeout = defaultTimeout;
        OcrCacheDir = string.IsNullOrWhiteSpace(ocrCacheDir) ? "ocr-cache" : ocrCacheDir.Trim();
        LocalOcrModel = string.IsNullOrWhiteSpace(localOcrModel) ? "ChineseV5" : localOcrModel.Trim();
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
    /// 获取本地 OCR 模型名称。
    /// </summary>
    public string LocalOcrModel { get; }
}
