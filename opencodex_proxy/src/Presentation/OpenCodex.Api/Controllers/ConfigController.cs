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

    [HttpPost("/config")]
    public IActionResult SaveConfig(ConfigSaveRequest request)
    {
        RequireUser();
        var result = _config.SaveConfig(request.ToDictionary());
        return Api(result);
    }
}
