using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义按渠道维护运行时熔断状态的服务。
/// </summary>
public interface IChannelCircuitBreakerService
{
    /// <summary>
    /// 获取渠道当前的健康状态。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <param name="enabled">渠道是否启用。</param>
    /// <param name="openDurationOverride">渠道级熔断时长覆盖；为零或负数表示不标记熔断状态。</param>
    /// <returns>运行时健康状态。</returns>
    ChannelHealthStatus GetHealthStatus(string ownerUsername, string channelId, bool enabled, TimeSpan? openDurationOverride = null);

    /// <summary>
    /// 在半开状态下尝试占用一个探测请求名额。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <param name="openDurationOverride">渠道级熔断时长覆盖；为零或负数表示不标记熔断状态。</param>
    /// <returns>成功占用则返回 <see langword="true"/>。</returns>
    bool TryAcquireHalfOpenProbe(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null);

    /// <summary>
    /// 释放一个未完成状态转换的半开探测名额。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    void ReleaseHalfOpenProbe(string ownerUsername, string channelId, TimeSpan? openDurationOverride = null);

    /// <summary>
    /// 记录一次成功请求。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    void RecordSuccess(string ownerUsername, string channelId);

    /// <summary>
    /// 记录一次失败请求。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    /// <param name="exception">失败异常。</param>
    /// <param name="openDurationOverride">渠道级熔断时长覆盖；为零或负数表示不标记熔断状态。</param>
    /// <returns>该失败是否计入熔断统计。</returns>
    bool RecordFailure(string ownerUsername, string channelId, Exception exception, TimeSpan? openDurationOverride = null);

    /// <summary>
    /// 重置渠道当前的熔断状态。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="channelId">渠道标识符。</param>
    void Reset(string ownerUsername, string channelId);
}
