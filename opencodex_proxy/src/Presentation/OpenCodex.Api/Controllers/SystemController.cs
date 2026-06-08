using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Controllers;

public sealed class SystemController : ApiControllerBase
{
    [HttpGet("/")]
    public IActionResult Root()
    {
        return ApiResponse(ApiOpResult<object>.Succeed(new
        {
            service = "OpenCodex API",
            status = "ok"
        }));
    }

    [HttpGet("/health")]
    public IActionResult Health()
    {
        return ApiResponse(ApiOpResult<object>.Succeed(new
        {
            status = "ok"
        }));
    }
}
