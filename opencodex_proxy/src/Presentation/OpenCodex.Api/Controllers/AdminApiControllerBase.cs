using Microsoft.AspNetCore.Mvc;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public abstract class AdminApiControllerBase : ApiControllerBase
{
    private readonly IAdminSessionService _adminSession;

    protected AdminApiControllerBase(IAdminSessionService adminSession)
    {
        _adminSession = adminSession;
    }

    protected AdminSessionUser RequireUser()
    {
        return RefreshSessionUser(_adminSession.RequireUser);
    }

    protected AdminSessionUser RequireSuperadmin()
    {
        return _adminSession.RequireSuperadmin(RequireUser());
    }

    protected IActionResult Api(ApiResult result, int? successStatusCode = null)
    {
        return ApiResponse(result, successStatusCode);
    }

    private AdminSessionUser RefreshSessionUser(
        Func<AdminSessionUser?, AdminSessionUser> require)
    {
        try
        {
            var user = require(AdminSession.CurrentUser(HttpContext));
            AdminSession.SetUser(HttpContext, user);
            return user;
        }
        catch (BadRequestException exception) when (exception.StatusCode == StatusCodes.Status401Unauthorized)
        {
            AdminSession.ClearUser(HttpContext);
            throw;
        }
    }
}
