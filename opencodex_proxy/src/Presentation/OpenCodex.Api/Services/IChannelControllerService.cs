using OpenCodex.CoreBase.DTOs.Channels;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 渠道管理服务：读取渠道运行时状态（含 id 过滤解析）。
/// </summary>
public interface IChannelControllerService
{
    ApiOpResult<ChannelRuntimeListResponse> ReadChannelRuntime(string? ids);
}
