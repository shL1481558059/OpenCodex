namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义按渠道限制主请求并发数的运行时服务。
/// </summary>
public interface IChannelCapacityService
{
    /// <summary>
    /// 尝试为指定渠道占用一个主请求并发槽位。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channel">渠道配置。</param>
    /// <returns>成功时返回可释放的占位对象；容量已满时返回 <see langword="null"/>。</returns>
    IChannelCapacityLease? TryAcquire(
        string ownerUsername,
        IReadOnlyDictionary<string, object?> channel);

    /// <summary>
    /// 获取当前渠道已占用的主请求并发数量。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <returns>当前已占用的并发数量。</returns>
    int GetActiveRequests(string ownerUsername, string channelId);
}

/// <summary>
/// 表示一次已成功占用的渠道并发槽位。
/// </summary>
public interface IChannelCapacityLease : IDisposable
{
}
