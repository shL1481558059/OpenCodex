using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected string TraceId()
    {
        return HttpContext.TraceIdentifier;
    }

    protected IActionResult ApiResponse(ApiResult result, int? successStatusCode = null)
    {
        var response = WithTraceId(result);
        if (response.Succeeded)
        {
            return successStatusCode is null
                ? Ok(response)
                : StatusCode(successStatusCode.Value, response);
        }

        return StatusCode(HttpStatusCode(response.Code), response);
    }

    private ApiResult WithTraceId(ApiResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.TraceId))
        {
            return result;
        }

        return result.WithTraceId(TraceId());
    }

    private static int HttpStatusCode(int code)
    {
        return code / 1000 switch
        {
            401 => StatusCodes.Status401Unauthorized,
            403 => StatusCodes.Status403Forbidden,
            404 => StatusCodes.Status404NotFound,
            502 => StatusCodes.Status502BadGateway,
            504 => StatusCodes.Status504GatewayTimeout,
            _ => code >= 500000
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status400BadRequest
        };
    }
}
