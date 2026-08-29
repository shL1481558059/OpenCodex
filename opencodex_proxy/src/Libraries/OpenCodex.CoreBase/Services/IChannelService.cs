using OpenCodex.CoreBase.DTOs.Channels;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义通道配置管理服务。
/// </summary>
public interface IChannelService
{
    /// <summary>
    /// 读取当前通道配置列表。
    /// </summary>
    ApiOpResult<ChannelListResponse> ReadChannels();

    /// <summary>
    /// 读取指定渠道的配置详情。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    ApiOpResult<ChannelResponse> ReadChannelById(Guid channelId);

    /// <summary>
    /// 读取渠道运行时状态快照。
    /// </summary>
    /// <param name="ids">可选的渠道标识符列表；为空时返回当前范围内的全部渠道。</param>
    ApiOpResult<ChannelRuntimeListResponse> ReadChannelRuntime(IReadOnlyList<Guid>? ids = null);

    /// <summary>
    /// 创建单个渠道。
    /// </summary>
    /// <param name="request">渠道请求。</param>
    Task<ApiOpResult<ChannelResponse>> CreateChannelAsync(ChannelRequest request);

    /// <summary>
    /// 更新单个渠道。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    /// <param name="request">渠道请求。</param>
    Task<ApiOpResult<ChannelResponse>> UpdateChannelAsync(Guid channelId, ChannelRequest request);

    /// <summary>
    /// 批量更新通道的低风险字段。
    /// </summary>
    /// <param name="request">批量更新请求。</param>
    Task<ApiOpResult<ChannelBatchUpdateResult>> BatchUpdateChannelsAsync(ChannelBatchUpdateRequest request);

    /// <summary>
    /// 删除单个渠道。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult<ChannelDeleteResult>> DeleteChannelAsync(Guid channelId);

    /// <summary>
    /// 合并导入通道配置。按 (owner_username, name) 匹配：已存在则更新，不存在则新增。
    /// </summary>
    /// <param name="body">导入的通道配置请求内容。</param>
    Task<ApiOpResult<ChannelBatchUpdateResult>> ImportChannelsAsync(
        IReadOnlyDictionary<string, object?> body);

    /// <summary>
    /// 重置指定渠道的运行时健康状态。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult<object>> ResetChannelHealthAsync(Guid channelId);

    /// <summary>
    /// 按渠道标识符读取当前登录用户 owner 范围内的渠道配置，供诊断使用。
    /// </summary>
    /// <param name="channelId">渠道标识符。</param>
    Task<ApiOpResult<ChannelDto>> ReadChannelForDiagnostics(Guid channelId);
}
