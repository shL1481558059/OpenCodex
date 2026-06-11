namespace OpenCodex.CoreBase.Services.Proxy;

/// <summary>
/// 定义会话与渠道的粘性亲和映射服务，用于让同一会话稳定路由到同一上游渠道以命中上游缓存。
/// </summary>
public interface IChannelAffinityService
{
    /// <summary>
    /// 获取指定会话此前命中的偏好渠道标识符。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="stickyKey">会话粘性键（来源于请求的 prompt_cache_key）。</param>
    /// <returns>仍在有效期内的偏好渠道标识符；当不存在或已过期时返回 <see langword="null"/>。</returns>
    string? GetPreferredChannelId(string ownerUsername, string stickyKey);

    /// <summary>
    /// 记录指定会话本次实际命中的渠道，并刷新其有效期（滑动过期）。
    /// </summary>
    /// <param name="ownerUsername">渠道所属用户名。</param>
    /// <param name="stickyKey">会话粘性键（来源于请求的 prompt_cache_key）。</param>
    /// <param name="channelId">本次实际命中的渠道标识符。</param>
    void Remember(string ownerUsername, string stickyKey, string channelId);
}

