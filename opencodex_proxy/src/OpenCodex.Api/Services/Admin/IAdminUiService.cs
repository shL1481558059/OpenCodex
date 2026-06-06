namespace OpenCodex.Api.Services;

public sealed class AdminUiFileResult
{
    public AdminUiFileResult(string path, string contentType)
    {
        Path = path;
        ContentType = contentType;
    }

    public string Path { get; }

    public string ContentType { get; }
}

public sealed class AdminUiHtmlResult
{
    public AdminUiHtmlResult(string html, string contentType)
    {
        Html = html;
        ContentType = contentType;
    }

    public string Html { get; }

    public string ContentType { get; }
}

public interface IAdminUiService
{
    AdminUiFileResult? GetSpaIndex();

    AdminUiFileResult? GetAsset(string? assetPath);

    AdminUiHtmlResult GetLoginPage(string? error = null);

    AdminUiHtmlResult GetAdminFallbackPage();
}
