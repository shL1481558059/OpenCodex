using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.Auth;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class SetupController : ApiControllerBase
{
    private readonly ISetupControllerService _setup;

    public SetupController(
        ISetupControllerService setup)
    {
        _setup = setup;
    }

    [HttpGet("/setup/status")]
    public IActionResult SetupStatus()
    {
        return ApiResponse(_setup.Status());
    }

    [HttpPost("/setup")]
    public async Task<IActionResult> Setup(SetupRequest request)
    {
        var result = await _setup.SetupAsync(request);
        return result.Succeeded
            ? ApiResponse(result, StatusCodes.Status201Created)
            : ApiResponse(result);
    }
}
