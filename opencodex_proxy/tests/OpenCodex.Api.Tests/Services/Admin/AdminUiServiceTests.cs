using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminUiServiceTests
{
    [Fact]
    public void GetSpaIndexUsesConfiguredStaticPath()
    {
        using var workspace = new TempWorkspace();
        var index = workspace.WriteStaticFile("index.html", "<!doctype html><div>spa</div>");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetSpaIndex();

        Assert.NotNull(result);
        Assert.Equal(index, result.Path);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
    }

    [Fact]
    public void GetAssetReturnsExistingAssetWithDetectedContentType()
    {
        using var workspace = new TempWorkspace();
        var asset = workspace.WriteStaticFile("assets/app.js", "console.log('spa');");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset("assets/app.js");

        Assert.NotNull(result);
        Assert.Equal(asset, result.Path);
        Assert.Equal("text/javascript", result.ContentType);
    }

    [Fact]
    public void GetAssetUsesOctetStreamForUnknownContentType()
    {
        using var workspace = new TempWorkspace();
        var asset = workspace.WriteStaticFile("assets/app.unknownext", "opaque");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset("assets/app.unknownext");

        Assert.NotNull(result);
        Assert.Equal(asset, result.Path);
        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public void GetAssetDetectsSvgContentType()
    {
        using var workspace = new TempWorkspace();
        var asset = workspace.WriteStaticFile("favicon.svg", "<svg></svg>");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset("favicon.svg");

        Assert.NotNull(result);
        Assert.Equal(asset, result.Path);
        Assert.Equal("image/svg+xml", result.ContentType);
    }

    [Fact]
    public void GetAssetFallsBackToSpaIndexWhenAssetIsMissing()
    {
        using var workspace = new TempWorkspace();
        var index = workspace.WriteStaticFile("index.html", "<!doctype html><div>spa</div>");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset("channels");

        Assert.NotNull(result);
        Assert.Equal(index, result.Path);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
    }

    [Fact]
    public void GetAssetDoesNotServePathTraversalOutsideStaticRoot()
    {
        using var workspace = new TempWorkspace();
        var index = workspace.WriteStaticFile("index.html", "<!doctype html><div>spa</div>");
        File.WriteAllText(Path.Combine(workspace.Path, "secret.txt"), "secret");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset("../secret.txt");

        Assert.NotNull(result);
        Assert.Equal(index, result.Path);
    }

    [Fact]
    public void GetAssetReturnsNullForEmptyAssetPath()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteStaticFile("index.html", "<!doctype html><div>spa</div>");
        var service = CreateService(staticPath: workspace.StaticPath);

        var result = service.GetAsset(" ");

        Assert.Null(result);
    }

    [Fact]
    public void GetLoginPageReturnsEncodedErrorHtml()
    {
        var service = CreateService();

        var result = service.GetLoginPage("<bad&secret>");

        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("OpenCodex Admin", result.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;bad&amp;secret&gt;", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<bad&secret>", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAdminFallbackPageReturnsMissingFrontendHtml()
    {
        var service = CreateService();

        var result = service.GetAdminFallbackPage();

        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("OpenCodex 管理台前端未构建", result.Html, StringComparison.Ordinal);
    }

    private static AdminUiService CreateService(
        string? staticPath = null)
    {
        return new AdminUiService(new StaticDirectoryProvider(
            Path.GetFullPath(staticPath ?? Directory.GetCurrentDirectory())));
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencodex-admin-ui-{Guid.NewGuid():N}");
            StaticPath = System.IO.Path.Combine(Path, "admin-static");
            Directory.CreateDirectory(StaticPath);
        }

        public string Path { get; }

        public string StaticPath { get; }

        public string WriteStaticFile(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(StaticPath, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class StaticDirectoryProvider : IAdminStaticDirectoryProvider
    {
        private readonly string _staticDirectory;

        public StaticDirectoryProvider(string staticDirectory)
        {
            _staticDirectory = staticDirectory;
        }

        public string GetStaticDirectory()
        {
            return _staticDirectory;
        }
    }
}
