using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Configuration;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SystemSettingsController : AuthenticatedApiControllerBase
{
    private readonly IDesktopSystemSettingsStore _settings;

    public SystemSettingsController(
        IWorkContext workContext,
        IDesktopSystemSettingsStore settings)
        : base(workContext)
    {
        _settings = settings;
    }

    [HttpGet("/system-settings")]
    public IActionResult GetSettings()
    {
        RequireSuperadmin();
        return Api(ApiOpResult<SystemSettingsResponse>.Succeed(_settings.Get()));
    }

    [HttpPut("/system-settings")]
    public IActionResult UpdateSettings(SystemSettingsUpdateRequest request)
    {
        RequireSuperadmin();
        try
        {
            var settings = _settings.Save(_settings.Normalize(request));
            return Api(ApiOpResult<SystemSettingsResponse>.Succeed(settings));
        }
        catch (ArgumentException exception)
        {
            return Api(ApiOpResult<SystemSettingsResponse>.Fail(400, exception.Message));
        }
    }
}
