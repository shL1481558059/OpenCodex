using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class WebSearchController : AuthenticatedApiControllerBase
{
    private readonly IWebSearchService _webSearch;

    public WebSearchController(
        IWorkContext workContext,
        IWebSearchService webSearch)
        : base(workContext)
    {
        _webSearch = webSearch;
    }

    [HttpGet("/web-search")]
    public IActionResult WebSearch()
    {
        RequireSuperadmin();
        var result = _webSearch.ReadConfig();
        return Api(result);
    }

    [HttpPost("/web-search")]
    public IActionResult SaveWebSearch(WebSearchConfigRequest request)
    {
        RequireSuperadmin();
        var result = _webSearch.SaveConfig(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/web-search/import")]
    public IActionResult ImportWebSearch(WebSearchConfigRequest request)
    {
        RequireSuperadmin();
        var result = _webSearch.ImportConfig(request.ToDictionary());
        return Api(result);
    }

    [HttpPost("/web-search/test-key")]
    public async Task<IActionResult> TestWebSearchKey(WebSearchTestKeyRequest request)
    {
        RequireSuperadmin();
        if (request.Id is null)
        {
            return Api(ApiOpResult<WebSearchTestKeyResponsePayload>.Fail(400, "id is required"));
        }

        var test = await _webSearch.TestKeyAsync(
            request.Id.Value,
            request.EffectiveQuery(),
            HttpContext.RequestAborted);
        return Api(test);
    }
}
