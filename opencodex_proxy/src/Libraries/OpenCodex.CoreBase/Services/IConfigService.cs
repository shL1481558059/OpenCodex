using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.DTOs;
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
    ApiOpResult<ConfigResponse> ReadConfig();

    /// <summary>
    /// 创建单个渠道。
    /// </summary>
    /// <param name="request">渠道请求。</param>
    Task<ApiOpResult<ConfigResponse>> CreateChannelAsync(ChannelRequest request);

    /// <summary>
    /// 更新单个渠道。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    /// <param name="request">渠道请求。</param>
    Task<ApiOpResult<ConfigResponse>> UpdateChannelAsync(Guid channelId, ChannelRequest request);

    /// <summary>
    /// 批量更新通道的低风险字段。
    /// </summary>
    /// <param name="request">批量更新请求。</param>
    Task<ApiOpResult<ConfigResponse>> BatchUpdateChannelsAsync(ChannelBatchUpdateRequest request);

    /// <summary>
    /// 删除单个渠道。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult<ConfigResponse>> DeleteChannelAsync(Guid channelId);

    /// <summary>
    /// 合并导入通道配置。按 (owner_username, name) 匹配：已存在则更新，不存在则新增。
    /// </summary>
    /// <param name="body">导入的通道配置请求内容。</param>
    Task<ApiOpResult<ConfigResponse>> ImportConfigAsync(
        IReadOnlyDictionary<string, object?> body);

    /// <summary>
    /// 重置指定渠道的运行时健康状态。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult> ResetChannelHealthAsync(Guid channelId);

    /// <summary>
    /// 按渠道标识符读取当前登录用户 owner 范围内的渠道配置，供诊断使用。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult<ChannelDto>> ReadChannelForDiagnostics(Guid channelId);
}
