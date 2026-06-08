using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台会话权限检查服务。
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// 要求当前上下文存在已登录用户。
    /// </summary>
    /// <param name="currentUser">当前会话用户。</param>
    /// <returns>已登录用户。</returns>
    SessionUser RequireUser(SessionUser? currentUser);

    /// <summary>
    /// 要求当前上下文存在超级管理员用户。
    /// </summary>
    /// <param name="currentUser">当前会话用户。</param>
    /// <returns>超级管理员用户。</returns>
    SessionUser RequireSuperadmin(SessionUser? currentUser);
}
