using OpenCodex.CoreBase.DTOs.AdminAuth;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminAuthService
{
    ApiResult<AdminSessionResponse> Login(string? username, string? password);
}
