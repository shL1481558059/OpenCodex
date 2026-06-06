using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.DTOs.AdminUsers;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminUsersController : AdminApiControllerBase
{
    private readonly IAdminUserService _adminUsers;

    public AdminUsersController(
        IAdminSessionService adminSession,
        IAdminUserService adminUsers,
        IRequestBodyReader bodyReader)
        : base(adminSession, bodyReader)
    {
        _adminUsers = adminUsers;
    }

    [HttpGet("/admin/api/users")]
    [ProducesResponseType(typeof(UsersResponse), StatusCodes.Status200OK)]
    public IActionResult Users()
    {
        RequireSuperadmin();
        var result = _adminUsers.ListUsers();
        return Ok(UsersResponse.From(result.Data));
    }

    [HttpPost("/admin/api/users")]
    [ProducesResponseType(typeof(UserResponsePayload), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateUser()
    {
        RequireSuperadmin();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminUsers.CreateUser(new AdminUserCreateCommand(
            JsonDictionaryValue.String(body, "username"),
            JsonDictionaryValue.String(body, "password"),
            JsonDictionaryValue.Get(body, "enabled") is not false));
        if (!result.Succeeded || result.Data is null)
        {
            return BadRequestError(result.Message);
        }

        return StatusCode(StatusCodes.Status201Created, UserResponsePayload.From(result.Data));
    }

    [HttpPatch("/admin/api/users/{username}")]
    [ProducesResponseType(typeof(UserResponsePayload), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateUser(string username)
    {
        RequireSuperadmin();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminUsers.UpdateUser(username, new AdminUserUpdateCommand(
            body.ContainsKey("enabled")
                ? JsonDictionaryValue.Get(body, "enabled") is true
                : null,
            body.ContainsKey("password")
                ? JsonDictionaryValue.String(body, "password")
                : null));
        if (!result.Succeeded || result.Data is null)
        {
            return result.Code == AdminUserErrorCodes.NotFound
                ? NotFoundError(result.Message)
                : BadRequestError(result.Message);
        }

        return Ok(UserResponsePayload.From(result.Data));
    }

    [HttpDelete("/admin/api/users/{username}")]
    [ProducesResponseType(typeof(DeleteUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult DeleteUser(string username)
    {
        var user = RequireSuperadmin();
        var result = _adminUsers.DeleteUser(username, user.Username);
        if (!result.Succeeded || result.Data is null)
        {
            return result.Code == AdminUserErrorCodes.NotFound
                ? NotFoundError(result.Message)
                : BadRequestError(result.Message);
        }

        return Ok(DeleteUserResponse.From(result.Data));
    }
}
