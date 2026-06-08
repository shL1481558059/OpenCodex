using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminApiKeys;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminApiKeysController : AdminApiControllerBase
{
    private readonly IAdminApiKeyService _adminApiKeys;

    public AdminApiKeysController(
        IAdminSessionService adminSession,
        IAdminApiKeyService adminApiKeys)
        : base(adminSession)
    {
        _adminApiKeys = adminApiKeys;
    }

    [HttpGet("/api-keys")]
    public IActionResult ApiKeys(string? owner_username)
    {
        var user = RequireUser();
        var result = _adminApiKeys.ListKeys(
            owner_username,
            user.Username,
            AdminSession.IsSuperadmin(user));

        return Api(result);
    }

    [HttpPost("/api-keys")]
    public IActionResult CreateApiKey(AdminApiKeyCreateRequest request)
    {
        var user = RequireUser();
        var result = _adminApiKeys.CreateKey(
            request.ToCommand(),
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Api(result, StatusCodes.Status201Created);
    }

    [HttpPatch("/api-keys/{keyId:long}")]
    public IActionResult UpdateApiKey(long keyId, AdminApiKeyUpdateRequest request)
    {
        var user = RequireUser();
        var result = _adminApiKeys.UpdateKey(
            keyId,
            request.ToCommand(),
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Api(result);
    }

    [HttpDelete("/api-keys/{keyId:long}")]
    public IActionResult DeleteApiKey(long keyId)
    {
        var user = RequireUser();
        var result = _adminApiKeys.DeleteKey(
            keyId,
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Api(result);
    }
}
