using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Users;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class UsersController : AuthenticatedApiControllerBase
{
    private readonly IUserService _users;

    public UsersController(
        IWorkContext workContext,
        IUserService users)
        : base(workContext)
    {
        _users = users;
    }

    [HttpGet("/users")]
    public IActionResult Users()
    {
        RequireSuperadmin();
        var result = _users.ListUsers();
        return Api(result);
    }

    [HttpPost("/users")]
    public IActionResult CreateUser(UserCreateRequest request)
    {
        RequireSuperadmin();
        var result = _users.CreateUser(request.ToCommand());
        return Api(result, StatusCodes.Status201Created);
    }

    [HttpPatch("/users/{username}")]
    public IActionResult UpdateUser(string username, UserUpdateRequest request)
    {
        RequireSuperadmin();
        var result = _users.UpdateUser(username, request.ToCommand());
        return Api(result);
    }

    [HttpDelete("/users/{username}")]
    public IActionResult DeleteUser(string username)
    {
        RequireSuperadmin();
        var result = _users.DeleteUser(username);
        return Api(result);
    }
}
