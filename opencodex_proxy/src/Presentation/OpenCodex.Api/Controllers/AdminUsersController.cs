using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminUsers;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminUsersController : AdminApiControllerBase
{
    private readonly IAdminUserService _adminUsers;

    public AdminUsersController(
        IAdminSessionService adminSession,
        IAdminUserService adminUsers)
        : base(adminSession)
    {
        _adminUsers = adminUsers;
    }

    [HttpGet("/users")]
    public IActionResult Users()
    {
        RequireSuperadmin();
        var result = _adminUsers.ListUsers();
        return Api(result);
    }

    [HttpPost("/users")]
    public IActionResult CreateUser(AdminUserCreateRequest request)
    {
        RequireSuperadmin();
        var result = _adminUsers.CreateUser(request.ToCommand());
        return Api(result, StatusCodes.Status201Created);
    }

    [HttpPatch("/users/{username}")]
    public IActionResult UpdateUser(string username, AdminUserUpdateRequest request)
    {
        RequireSuperadmin();
        var result = _adminUsers.UpdateUser(username, request.ToCommand());
        return Api(result);
    }

    [HttpDelete("/users/{username}")]
    public IActionResult DeleteUser(string username)
    {
        var user = RequireSuperadmin();
        var result = _adminUsers.DeleteUser(username, user.Username);
        return Api(result);
    }
}
