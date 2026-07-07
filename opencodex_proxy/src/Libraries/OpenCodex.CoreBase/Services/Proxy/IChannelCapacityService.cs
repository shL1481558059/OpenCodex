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
    /// <param name="requestModel">请求模型。</param>
    /// <param name="upstreamModel">上游模型。</param>
    /// <returns>成功时返回可释放的占位对象；容量已满时返回 <see langword="null"/>。</returns>
    Task<IChannelCapacityLease?> TryAcquireAsync(
        string ownerUsername,
        IReadOnlyDictionary<string, object?> channel,
        string? requestModel = null,
        string? upstreamModel = null);

    /// <summary>
    /// 获取当前渠道已占用的主请求并发数量。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <returns>当前已占用的并发数量。</returns>
    int GetActiveRequests(string ownerUsername, string channelId);

    /// <summary>
    /// 获取当前渠道按请求模型和上游模型聚合后的并发数量。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <returns>当前按模型对聚合后的并发数量。</returns>
    IReadOnlyList<ChannelActiveModelUsage> GetActiveModelUsages(string ownerUsername, string channelId);
}

/// <summary>
/// 表示渠道运行时并发中某个模型对的占用数量。
/// </summary>
public sealed class ChannelActiveModelUsage(string? model, string? upstreamModel, int activeRequests)
{
    /// <summary>
    /// 获取请求模型。
    /// </summary>
    public string? Model { get; } = model;

    /// <summary>
    /// 获取上游模型。
    /// </summary>
    public string? UpstreamModel { get; } = upstreamModel;

    /// <summary>
    /// 获取当前占用的主请求并发数量。
    /// </summary>
    public int ActiveRequests { get; } = activeRequests;
}

/// <summary>
/// 表示一次已成功占用的渠道并发槽位。
/// </summary>
public interface IChannelCapacityLease : IDisposable
{
}
