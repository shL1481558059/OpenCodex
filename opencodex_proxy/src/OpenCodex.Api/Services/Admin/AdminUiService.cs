using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Services;

public sealed class AdminUiService : IAdminUiService
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    private readonly IAdminStaticDirectoryProvider _staticDirectoryProvider;

    public AdminUiService(IAdminStaticDirectoryProvider staticDirectoryProvider)
    {
        _staticDirectoryProvider = staticDirectoryProvider;
    }

    public AdminUiFileResult? GetSpaIndex()
    {
        return GetSpaIndex(AdminStaticDirectory());
    }

    public AdminUiFileResult? GetAsset(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return null;
        }

        var staticDir = AdminStaticDirectory();
        var asset = SafeAssetPath(staticDir, assetPath);
        if (asset is null || !File.Exists(asset))
        {
            return GetSpaIndex(staticDir);
        }

        return new AdminUiFileResult(asset, ContentTypeFor(asset));
    }

    public AdminUiHtmlResult GetLoginPage(string? error = null)
    {
        var errorHtml = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"<p class=\"error\">{HtmlEncode(error)}</p>";
        return new AdminUiHtmlResult($$"""
            <!doctype html>
            <html lang="zh-CN">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>OpenCodex Admin</title>
                <link rel="icon" type="image/svg+xml" href="/admin/favicon.svg">
              </head>
              <body>
                <main>
                  <h1>OpenCodex Admin</h1>
                  <p>管理渠道、模型映射与请求日志。</p>
                  <form method="post">
                    {{errorHtml}}
                    <label for="password">管理密码</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" autofocus>
                    <button type="submit">进入控制台</button>
                  </form>
                </main>
              </body>
            </html>
            """, HtmlContentType);
    }

    public AdminUiHtmlResult GetAdminFallbackPage()
    {
        return new AdminUiHtmlResult("""
            <!doctype html>
            <html lang="zh-CN">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>OpenCodex Admin</title>
                <link rel="icon" type="image/svg+xml" href="/admin/favicon.svg">
              </head>
              <body>
                <main>
                  <h1>OpenCodex 管理台前端未构建</h1>
                  <p>请先执行 <code>npm run build</code> 生成 Vue 管理台静态文件。</p>
                </main>
              </body>
            </html>
            """, HtmlContentType);
    }

    private string AdminStaticDirectory()
    {
        return _staticDirectoryProvider.GetStaticDirectory();
    }

    private static AdminUiFileResult? GetSpaIndex(string staticDir)
    {
        var index = Path.Combine(staticDir, "index.html");
        return File.Exists(index)
            ? new AdminUiFileResult(index, HtmlContentType)
            : null;
    }

    private static string? SafeAssetPath(string staticDir, string assetPath)
    {
        var root = Path.GetFullPath(staticDir);
        var candidate = Path.GetFullPath(Path.Combine(root, assetPath));
        var requiredPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(requiredPrefix, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css",
            ".html" or ".htm" => HtmlContentType,
            ".ico" => "image/x-icon",
            ".js" or ".mjs" => "text/javascript",
            ".json" or ".map" => "application/json",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".wasm" => "application/wasm",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    private static string HtmlEncode(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}
