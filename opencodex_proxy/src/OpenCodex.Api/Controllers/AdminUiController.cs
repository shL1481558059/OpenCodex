using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminUiController : ApiControllerBase
{
    private readonly IAdminUiService _adminUiService;
    private readonly IAdminAuthService _authService;

    public AdminUiController(
        IAdminUiService adminUiService,
        IAdminAuthService authService)
    {
        _adminUiService = adminUiService;
        _authService = authService;
    }

    [HttpGet("/admin")]
    public IActionResult Admin()
    {
        var index = _adminUiService.GetSpaIndex();
        if (index is not null)
        {
            return PhysicalFile(index.Path, index.ContentType);
        }

        return AdminSession.CurrentUser(HttpContext) is null
            ? Html(_adminUiService.GetLoginPage())
            : Html(_adminUiService.GetAdminFallbackPage());
    }

    [HttpPost("/admin")]
    public async Task<IActionResult> AdminLogin()
    {
        var form = Request.HasFormContentType
            ? await Request.ReadFormAsync()
            : null;
        var username = form?["username"].ToString().Trim() ?? string.Empty;
        var password = form?["password"].ToString().Trim() ?? string.Empty;
        var result = _authService.Login(username, password);
        if (result.Succeeded && result.Data is not null)
        {
            AdminSession.SetUser(
                HttpContext,
                new AdminSessionUser(result.Data.Username, result.Data.Role, result.Data.Enabled));
            return Redirect("/admin");
        }

        return Html(_adminUiService.GetLoginPage(result.Message));
    }

    [HttpGet("/admin/{**assetPath}")]
    public IActionResult AdminAsset(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return Redirect("/admin");
        }

        var asset = _adminUiService.GetAsset(assetPath);
        if (asset is not null)
        {
            return PhysicalFile(asset.Path, asset.ContentType);
        }

        return Redirect("/admin");
    }

    private ContentResult Html(AdminUiHtmlResult result)
    {
        return Content(result.Html, result.ContentType);
    }
}
