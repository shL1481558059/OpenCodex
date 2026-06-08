using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Users;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台用户管理服务。
/// </summary>
public interface IUserService
{
    /// <summary>
    /// 读取后台用户列表。
    /// </summary>
    /// <returns>后台用户列表结果。</returns>
    ApiOpResult<UsersResponse> ListUsers();

    /// <summary>
    /// 创建后台用户。
    /// </summary>
    /// <param name="command">创建用户命令。</param>
    /// <returns>创建后的用户结果。</returns>
    ApiOpResult<UserResponsePayload> CreateUser(
        UserCreateCommand command);

    /// <summary>
    /// 更新后台用户。
    /// </summary>
    /// <param name="username">要更新的用户名。</param>
    /// <param name="command">更新用户命令。</param>
    /// <returns>更新后的用户结果。</returns>
    ApiOpResult<UserResponsePayload> UpdateUser(
        string username,
        UserUpdateCommand command);

    /// <summary>
    /// 删除后台用户。
    /// </summary>
    /// <param name="username">要删除的用户名。</param>
    /// <returns>删除操作结果。</returns>
    ApiOpResult<DeleteUserResponse> DeleteUser(
        string username);
}
