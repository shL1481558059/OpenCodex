using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected string TraceId()
    {
        return HttpContext.TraceIdentifier;
    }
}
