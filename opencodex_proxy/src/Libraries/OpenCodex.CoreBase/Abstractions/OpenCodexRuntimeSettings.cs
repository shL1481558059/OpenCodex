namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 表示代理服务所需的运行时设置。
/// </summary>
public sealed class OpenCodexRuntimeSettings
{
    /// <summary>
    /// 初始化 <see cref="OpenCodexRuntimeSettings"/> 类的新实例。
    /// </summary>
    /// <param name="dbPath">SQLite 数据库路径。</param>
    /// <param name="adminUsername">配置的管理员用户名。</param>
    /// <param name="adminPassword">配置的管理员密码。</param>
    /// <param name="defaultTimeout">默认上游请求超时时间，单位为秒。</param>
    /// <param name="ocrCacheDir">OCR 缓存目录。</param>
    /// <param name="localOcrModel">本地 OCR 模型名称。</param>
    public OpenCodexRuntimeSettings(
        string dbPath,
        string adminUsername,
        string adminPassword,
        int defaultTimeout,
        string? ocrCacheDir = null,
        string? localOcrModel = null)
    {
        DbPath = dbPath;
        AdminUsername = adminUsername;
        AdminPassword = adminPassword;
        DefaultTimeout = defaultTimeout;
        OcrCacheDir = string.IsNullOrWhiteSpace(ocrCacheDir) ? "ocr-cache" : ocrCacheDir.Trim();
        LocalOcrModel = string.IsNullOrWhiteSpace(localOcrModel) ? "ChineseV5" : localOcrModel.Trim();
    }

    /// <summary>
    /// 获取运行时使用的轻量级数据库路径。
    /// </summary>
    public string DbPath { get; }

    /// <summary>
    /// 获取配置的管理员用户名。
    /// </summary>
    public string AdminUsername { get; }

    /// <summary>
    /// 获取配置的管理员密码。
    /// </summary>
    public string AdminPassword { get; }

    /// <summary>
    /// 获取默认上游请求超时时间，单位为秒。
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
