using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public abstract class AdminApiControllerBase : ApiControllerBase
{
    private readonly IAdminSessionService _adminSession;
    private readonly IRequestBodyReader? _bodyReader;

    protected AdminApiControllerBase(
        IAdminSessionService adminSession,
        IRequestBodyReader? bodyReader = null)
    {
        _adminSession = adminSession;
        _bodyReader = bodyReader;
    }

    protected string? QueryValue(string key)
    {
        return Request.Query.TryGetValue(key, out var values) ? values.ToString() : null;
    }

    protected async Task<Dictionary<string, object?>?> BodyObject()
    {
        if (_bodyReader is null)
        {
            throw new InvalidOperationException("request body reader is not configured for this controller");
        }

        return await _bodyReader.ReadJsonObjectAsync(
            Request,
            HttpContext.RequestAborted);
    }

    protected AdminSessionUser RequireUser()
    {
        return RefreshSessionUser(_adminSession.RequireUser);
    }

    protected AdminSessionUser RequireSuperadmin()
    {
        return _adminSession.RequireSuperadmin(RequireUser());
    }

    protected static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }

    protected BadRequestObjectResult BadRequestError(string message)
    {
        return BadRequest(new AdminErrorResponse(message));
    }

    protected NotFoundObjectResult NotFoundError(string message)
    {
        return NotFound(new AdminErrorResponse(message));
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
