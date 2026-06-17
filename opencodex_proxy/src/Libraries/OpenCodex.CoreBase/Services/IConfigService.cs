using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义通道配置管理服务。
/// </summary>
public interface IConfigService
{
    /// <summary>
    /// 读取当前通道配置。
    /// </summary>
    /// <returns>通道配置结果。</returns>
    ApiOpResult<ConfigResponse> ReadConfig();

    /// <summary>
    /// 保存通道配置。
    /// </summary>
    /// <param name="body">通道配置请求内容。</param>
    /// <returns>保存后的通道配置结果。</returns>
    ApiOpResult<ConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body);
}
