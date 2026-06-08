using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.AdminWebSearch;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services.Admin;

namespace OpenCodex.Api.Controllers;

public sealed class AdminWebSearchController : AdminApiControllerBase
{
    private readonly IAdminWebSearchService _adminWebSearch;

    public AdminWebSearchController(
        IAdminSessionService adminSession,
        IAdminWebSearchService adminWebSearch)
        : base(adminSession)
    {
        _adminWebSearch = adminWebSearch;
    }

    [HttpGet("/web-search")]
    public IActionResult WebSearch()
    {
        RequireSuperadmin();
        var result = _adminWebSearch.ReadConfig();
        return Api(result);
    }

    [HttpPost("/web-search")]
    public IActionResult SaveWebSearch(WebSearchConfigRequest request)
    {
        RequireSuperadmin();
        var result = _adminWebSearch.SaveConfig(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/web-search/test-key")]
    public async Task<IActionResult> TestWebSearchKey(WebSearchTestKeyRequest request)
    {
        RequireSuperadmin();
        if (request.Id is null)
        {
            return Api(ApiResult.Fail<WebSearchTestKeyResponsePayload>("id is required"));
        }

        var test = await _adminWebSearch.TestKeyAsync(
            request.Id.Value,
            request.EffectiveQuery(),
            HttpContext.RequestAborted);
        return Api(test);
    }
}
