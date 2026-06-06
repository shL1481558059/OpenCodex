using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.DTOs.AdminApiKeys;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminApiKeysController : AdminApiControllerBase
{
    private readonly IAdminApiKeyService _adminApiKeys;

    public AdminApiKeysController(
        IAdminSessionService adminSession,
        IAdminApiKeyService adminApiKeys,
        IRequestBodyReader bodyReader)
        : base(adminSession, bodyReader)
    {
        _adminApiKeys = adminApiKeys;
    }

    [HttpGet("/admin/api/api-keys")]
    [ProducesResponseType(typeof(ApiKeysResponse), StatusCodes.Status200OK)]
    public IActionResult ApiKeys()
    {
        var user = RequireUser();
        var result = _adminApiKeys.ListKeys(
            QueryValue("owner_username"),
            user.Username,
            AdminSession.IsSuperadmin(user));

        return Ok(ApiKeysResponse.From(result.Data));
    }

    [HttpPost("/admin/api/api-keys")]
    [ProducesResponseType(typeof(ApiKeyResponsePayload), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateApiKey()
    {
        var user = RequireUser();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminApiKeys.CreateKey(
            new AdminApiKeyCreateCommand(
                JsonDictionaryValue.String(body, "owner_username"),
                JsonDictionaryValue.String(body, "name")),
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return BadRequestError(result.Message);
        }

        return StatusCode(StatusCodes.Status201Created, ApiKeyResponsePayload.From(result.Data));
    }

    [HttpPatch("/admin/api/api-keys/{keyId:long}")]
    [ProducesResponseType(typeof(ApiKeyResponsePayload), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateApiKey(long keyId)
    {
        var user = RequireUser();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminApiKeys.UpdateKey(
            keyId,
            new AdminApiKeyUpdateCommand(JsonDictionaryValue.Get(body, "enabled") is true),
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return result.Code == AdminApiKeyErrorCodes.NotFound
                ? NotFoundError(result.Message)
                : BadRequestError(result.Message);
        }

        return Ok(ApiKeyResponsePayload.From(result.Data));
    }

    [HttpDelete("/admin/api/api-keys/{keyId:long}")]
    [ProducesResponseType(typeof(DeleteApiKeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult DeleteApiKey(long keyId)
    {
        var user = RequireUser();
        var result = _adminApiKeys.DeleteKey(
            keyId,
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded)
        {
            return result.Code == AdminApiKeyErrorCodes.NotFound
                ? NotFoundError(result.Message)
                : BadRequestError(result.Message);
        }

        return Ok(new DeleteApiKeyResponse(true));
    }
}
