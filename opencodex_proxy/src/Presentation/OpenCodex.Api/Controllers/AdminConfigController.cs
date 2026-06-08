using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminConfig;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminConfigController : AdminApiControllerBase
{
    private readonly IAdminConfigService _adminConfig;

    public AdminConfigController(
        IAdminSessionService adminSession,
        IAdminConfigService adminConfig)
        : base(adminSession)
    {
        _adminConfig = adminConfig;
    }

    [HttpGet("/config")]
    public IActionResult Config()
    {
        var user = RequireUser();
        var result = _adminConfig.ReadConfig(user.Username, AdminSession.IsSuperadmin(user));
        return Api(result);
    }

    [HttpGet("/config/export")]
    public IActionResult ExportConfig()
    {
        var user = RequireUser();
        var result = _adminConfig.ExportConfig(user.Username, AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return Api(result);
        }

        var export = result.Data;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
        return Content(export.Payload, export.ContentType);
    }

    [HttpPost("/config/import")]
    public IActionResult ImportConfig(ConfigSaveRequest request)
    {
        var user = RequireUser();
        var result = _adminConfig.ImportConfig(
            request.ToDictionary(),
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Api(result);
    }

    [HttpPost("/config")]
    public IActionResult SaveConfig(ConfigSaveRequest request)
    {
        var user = RequireUser();
        var result = _adminConfig.SaveConfig(
            request.ToDictionary(),
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Api(result);
    }
}
