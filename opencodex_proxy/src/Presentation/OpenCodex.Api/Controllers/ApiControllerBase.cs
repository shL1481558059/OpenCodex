using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult ApiResponse(ApiOpResult result, int? successStatusCode = null)
    {
        if (result.Succeeded)
        {
            return successStatusCode is null
                ? Ok(result)
                : StatusCode(successStatusCode.Value, result);
        }

        return StatusCode(result.Code, result);
    }

}
