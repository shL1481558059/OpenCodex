using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 会话管理服务：当前会话、登录、登出。
/// </summary>
public interface ISessionControllerService
{
    Task<ApiOpResult<SessionResponse>> CurrentSessionAsync();

    Task<ApiOpResult<SessionResponse>> LoginAsync(string? username, string? password);

    Task<ApiOpResult<SessionResponse>> LogoutAsync();
}
