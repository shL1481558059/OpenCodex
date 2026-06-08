using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Config;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ConfigController : AuthenticatedApiControllerBase
{
    private readonly IConfigService _config;

    public ConfigController(
        IWorkContext workContext,
        IConfigService config)
        : base(workContext)
    {
        _config = config;
    }

    [HttpGet("/config")]
    public IActionResult Config()
    {
        RequireUser();
        var result = _config.ReadConfig();
        return Api(result);
    }

    [HttpGet("/config/export")]
    public IActionResult ExportConfig()
    {
        RequireUser();
        var result = _config.ExportConfig();
        if (!result.Succeeded || result.Payload is null)
        {
            return Api(result);
        }

        var export = result.Payload;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{export.FileName}\"";
        return Content(export.Payload, export.ContentType);
    }

    [HttpPost("/config/import")]
    public IActionResult ImportConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = _config.ImportConfig(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/config")]
    public IActionResult SaveConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = _config.SaveConfig(request.ToDictionary());
        return Api(result);
    }
}
