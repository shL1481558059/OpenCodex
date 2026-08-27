namespace OpenCodex.CoreBase.Caching;

/// <summary>
/// 集中管理缓存键格式,避免读缓存与失效两侧各自拼接字符串导致漂移。
/// 带参数的 key 以方法形式提供;全局前缀由缓存实现统一挂载,此处只写逻辑键。
/// </summary>
public static class CacheKeys
{
    /// <summary>鉴权:按 hash 缓存的 AccessApiKey 快照。</summary>
    public static string AuthApiKey(string hash) => $"auth:apikey:{hash}";

    /// <summary>鉴权:按 userId 缓存的 User 快照。</summary>
    public static string AuthUser(Guid userId) => $"auth:user:{userId}";

    /// <summary>渠道路由:按 ownerUsername 缓存的该用户启用渠道原始实体集。</summary>
    public static string RouteChannels(string ownerUsername) => $"route:channels:{ownerUsername}";

    /// <summary>管理台:全量渠道配置快照(进程内 IMemoryCache),供 SSE 每帧回查免落库。</summary>
    public static string ChannelConfig => "admin:channel-config";

    /// <summary>观测:全量渠道轻量快照(进程内 IMemoryCache),只含路由/容量所需字段,不含 models/headers/compat。</summary>
    public static string ChannelObservation => "admin:channel-observation";

    /// <summary>定价:按 (channelId, upstreamModel) 缓存的计费解析结果。</summary>
    /// <param name="redisVersion">Redis 全局定价版本;用于跨实例失效。</param>
    /// <param name="localVersion">进程内定价版本;用于 Redis 故障期间失效。</param>
    public static string PricingContext(
        int redisVersion,
        int localVersion,
        Guid? channelId,
        string? upstreamModel)
        => $"pricing:context:r{redisVersion}:l{localVersion}:{channelId}:{upstreamModel}";
}
