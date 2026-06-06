using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.DTOs.AdminConfig;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminConfigController : AdminApiControllerBase
{
    private readonly IAdminConfigService _adminConfig;

    public AdminConfigController(
        IAdminSessionService adminSession,
        IAdminConfigService adminConfig,
        IRequestBodyReader bodyReader)
        : base(adminSession, bodyReader)
    {
        _adminConfig = adminConfig;
    }

    [HttpGet("/admin/api/config")]
    [ProducesResponseType(typeof(ConfigResponse), StatusCodes.Status200OK)]
    public IActionResult Config()
    {
        var user = RequireUser();
        var result = _adminConfig.ReadConfig(user.Username, AdminSession.IsSuperadmin(user));
        return Ok(ConfigResponse.From(result.Data!));
    }

    [HttpGet("/admin/api/config/export")]
    [ProducesResponseType(typeof(ConfigResponse), StatusCodes.Status200OK)]
    public IActionResult ExportConfig()
    {
        var user = RequireUser();
        var result = _adminConfig.ReadConfig(user.Username, AdminSession.IsSuperadmin(user));
        var export = ConfigExportResponse.From(result.Data!);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
        return Content(export.Payload, export.ContentType);
    }

    [HttpPost("/admin/api/config/import")]
    [ProducesResponseType(typeof(ConfigImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportConfig()
    {
        var user = RequireUser();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminConfig.ImportConfig(
            body,
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return BadRequestError(result.Message);
        }

        return Ok(ConfigImportResponse.From(
            result.Data.Config,
            result.Data.Imported,
            result.Data.Skipped,
            result.Data.SkippedIds));
    }

    [HttpPost("/admin/api/config")]
    [ProducesResponseType(typeof(ConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveConfig()
    {
        var user = RequireUser();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminConfig.SaveConfig(
            body,
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return BadRequestError(result.Message);
        }

        return Ok(ConfigResponse.From(result.Data));
    }
}
