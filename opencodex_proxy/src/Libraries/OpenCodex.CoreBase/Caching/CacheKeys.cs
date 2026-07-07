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

    /// <summary>定价:按 (channelId, upstreamModel) 缓存的计费解析结果。</summary>
    public static string PricingContext(Guid? channelId, string? upstreamModel)
        => $"pricing:context:{channelId}:{upstreamModel}";
}
