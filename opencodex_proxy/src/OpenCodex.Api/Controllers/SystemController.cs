using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Controllers;

public sealed class SystemController : ApiControllerBase
{
    [HttpGet("/")]
    public IActionResult Root()
    {
        return Redirect("/admin");
    }

    [HttpGet("/health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "ok"
        });
    }
}
