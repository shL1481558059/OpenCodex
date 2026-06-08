using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public abstract class AuthenticatedApiControllerBase : ApiControllerBase
{
    private readonly IWorkContext _workContext;

    protected AuthenticatedApiControllerBase(IWorkContext workContext)
    {
        _workContext = workContext;
    }

    protected SessionUser RequireUser()
    {
        return _workContext.RequireUser();
    }

    protected SessionUser RequireSuperadmin()
    {
        return _workContext.RequireSuperadmin();
    }

    protected IActionResult Api(ApiOpResult result, int? successStatusCode = null)
    {
        return ApiResponse(result, successStatusCode);
    }

}
