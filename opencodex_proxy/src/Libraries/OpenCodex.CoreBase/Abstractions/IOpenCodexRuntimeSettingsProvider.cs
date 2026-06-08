namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 提供当前代理运行时设置的访问入口。
/// </summary>
public interface IOpenCodexRuntimeSettingsProvider
{
    /// <summary>
    /// 获取代理服务使用的运行时设置。
    /// </summary>
    /// <returns>当前运行时设置。</returns>
    OpenCodexRuntimeSettings GetSettings();
}
