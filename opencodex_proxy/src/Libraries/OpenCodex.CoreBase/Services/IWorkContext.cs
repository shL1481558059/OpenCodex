using OpenCodex.CoreBase.Domain;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义当前工作上下文的用户访问入口。
/// </summary>
public interface IWorkContext
{
    /// <summary>
    /// 获取当前会话用户。
    /// </summary>
    SessionUser? CurrentUser { get; }

    /// <summary>
    /// 获取当前上下文是否已登录。
    /// </summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// 获取当前用户是否为超级管理员。
    /// </summary>
    bool IsSuperadmin { get; }

    /// <summary>
    /// 要求当前上下文存在已登录用户。
    /// </summary>
    /// <returns>已登录用户。</returns>
    SessionUser RequireUser();

    /// <summary>
    /// 要求当前上下文存在超级管理员用户。
    /// </summary>
    /// <returns>超级管理员用户。</returns>
    SessionUser RequireSuperadmin();
}
