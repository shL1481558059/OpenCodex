using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.CoreBase.Services;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class WebSearchController : AuthenticatedApiControllerBase
{
    private readonly IWebSearchService _webSearch;
    private readonly IWebSearchAdminService _webSearchAdmin;

    public WebSearchController(
        IWorkContext workContext,
        IWebSearchService webSearch,
        IWebSearchAdminService webSearchAdmin)
        : base(workContext)
    {
        _webSearch = webSearch;
        _webSearchAdmin = webSearchAdmin;
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
        var test = await _webSearchAdmin.TestKeyAsync(request, HttpContext.RequestAborted);
        return Api(test);
    }
}
