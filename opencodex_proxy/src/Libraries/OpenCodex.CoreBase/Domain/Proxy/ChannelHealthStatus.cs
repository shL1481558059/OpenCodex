namespace OpenCodex.CoreBase.Domain.Proxy;

/// <summary>
/// 表示渠道的运行时健康状态。
/// </summary>
public enum ChannelHealthStatus
{
    Disabled = 0,
    Healthy = 1,
    Open = 2,
    HalfOpen = 3
}
