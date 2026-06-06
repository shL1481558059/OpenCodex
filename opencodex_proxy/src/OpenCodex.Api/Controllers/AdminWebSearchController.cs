using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.DTOs.AdminWebSearch;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminWebSearchController : AdminApiControllerBase
{
    private readonly IAdminWebSearchService _adminWebSearch;

    public AdminWebSearchController(
        IAdminSessionService adminSession,
        IAdminWebSearchService adminWebSearch,
        IRequestBodyReader bodyReader)
        : base(adminSession, bodyReader)
    {
        _adminWebSearch = adminWebSearch;
    }

    [HttpGet("/admin/api/web-search")]
    [ProducesResponseType(typeof(WebSearchConfigResponse), StatusCodes.Status200OK)]
    public IActionResult WebSearch()
    {
        RequireSuperadmin();
        var result = _adminWebSearch.ReadConfig();
        return Ok(WebSearchConfigResponse.From(result.Data!));
    }

    [HttpPost("/admin/api/web-search")]
    [ProducesResponseType(typeof(WebSearchConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveWebSearch()
    {
        RequireSuperadmin();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var result = _adminWebSearch.SaveConfig(body);
        if (!result.Succeeded || result.Data is null)
        {
            return BadRequestError(result.Message);
        }

        return Ok(WebSearchConfigResponse.From(result.Data));
    }

    [HttpPost("/admin/api/web-search/test-key")]
    [ProducesResponseType(typeof(WebSearchTestKeyResponsePayload), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestWebSearchKey()
    {
        RequireSuperadmin();
        var started = Stopwatch.GetTimestamp();
        var body = await BodyObject();
        if (body is null)
        {
            return BadRequestError("request body must be a JSON object");
        }

        var request = WebSearchTestKeyRequest.From(body);
        if (request.KeyId is null)
        {
            return BadRequestError("id is required");
        }

        var test = await _adminWebSearch.TestKeyAsync(
            request.KeyId.Value,
            request.Query,
            HttpContext.RequestAborted);
        if (!test.Succeeded || test.Data is null)
        {
            return BadRequestError(test.Message);
        }

        return Ok(WebSearchTestKeyResponsePayload.From(
            test.Data.Key,
            test.Data.Result,
            test.Data.Config,
            ElapsedMilliseconds(started)));
    }
}
