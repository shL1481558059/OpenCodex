using OpenCodex.CoreBase.DTOs.Channels;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 渠道管理实现：读取运行时状态，并把 id 过滤字符串解析为 Guid 列表。
/// </summary>
public sealed class ChannelControllerService : IChannelControllerService
{
    private readonly IChannelService _channels;

    public ChannelControllerService(IChannelService channels)
    {
        _channels = channels;
    }

    public ApiOpResult<ChannelRuntimeListResponse> ReadChannelRuntime(string? ids)
    {
        IReadOnlyList<Guid>? idList = null;
        if (!string.IsNullOrWhiteSpace(ids))
        {
            // 宽松解析: 无法解析为 Guid 的值会被静默忽略, 不返回错误。
            idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty)
                .ToList();
            if (idList.Count == 0)
            {
                idList = null;
            }
        }

        return _channels.ReadChannelRuntime(idList);
    }
}
