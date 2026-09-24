namespace OpenCodex.Api.Authentication;

/// <summary>
/// 定义代理端点访问密钥认证方案使用的常量。
/// </summary>
public static class ProxyBearerAuthenticationDefaults
{
    /// <summary>
    /// 代理端点访问密钥认证方案名称。
    /// </summary>
    public const string Scheme = "OpenCodexProxyBearer";

    /// <summary>
    /// 凭据缺失或不可用时返回的错误消息。
    /// </summary>
    /// <remarks>
    /// 消息文本保持历史值不变，客户端按此文本做兼容判断，不随凭据来源扩展而变化。
    /// </remarks>
    public const string InvalidCredentialMessage = "valid bearer api key required";
}
