using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Config;
using OpenCodex.Api.DTOs.AdminChannelDiagnostics;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminChannelDiagnosticsController : AdminApiControllerBase
{
    private readonly IAdminChannelDiagnosticsService _channelDiagnostics;

    public AdminChannelDiagnosticsController(
        IAdminSessionService adminSession,
        IAdminChannelDiagnosticsService channelDiagnostics,
        IRequestBodyReader bodyReader)
        : base(adminSession, bodyReader)
    {
        _channelDiagnostics = channelDiagnostics;
    }

    [HttpPost("/admin/api/channels/discover-models")]
    [HttpPost("/admin/api/discover-models")]
    public async Task<IActionResult> DiscoverModels()
    {
        RequireUser();
        var started = Stopwatch.GetTimestamp();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        try
        {
            var result = await _channelDiagnostics.DiscoverModelsAsync(
                body,
                HttpContext.RequestAborted);
            return Ok(DiscoverModelsResponse.From(
                result.Models,
                result.Raw,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return BadRequestError(exception.Message);
        }
        catch (UpstreamException exception)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new DiscoverModelsErrorResponse(
                    exception.Message,
                    exception.StatusCode,
                    ElapsedMilliseconds(started),
                    exception.Body));
        }
    }

    [HttpPost("/admin/api/channels/test")]
    [HttpPost("/admin/api/test-channel")]
    public async Task<IActionResult> TestChannel()
    {
        RequireUser();
        var started = Stopwatch.GetTimestamp();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        try
        {
            var result = await _channelDiagnostics.TestChannelAsync(
                body,
                HttpContext.RequestAborted);

            return Ok(TestChannelResponse.From(
                result.Model,
                result.UpstreamModel,
                result.Compat,
                result.Response,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return BadRequestError(exception.Message);
        }
        catch (ProxyException exception)
        {
            return Ok(new TestChannelErrorResponse(
                false,
                exception.StatusCode,
                ElapsedMilliseconds(started),
                exception.Message,
                exception is UpstreamException upstreamException
                    ? upstreamException.Body
                    : null));
        }
    }
}
