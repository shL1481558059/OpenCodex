using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenCodex.Api.Configuration;

namespace OpenCodex.Api.Tests.Configuration;

public sealed class AdminStaticDirectoryProviderTests
{
    [Fact]
    public void GetStaticDirectoryUsesConfiguredStaticPath()
    {
        using var workspace = new TempWorkspace();
        var provider = CreateProvider(staticPath: workspace.StaticPath);

        var result = provider.GetStaticDirectory();

        Assert.Equal(Path.GetFullPath(workspace.StaticPath), result);
    }

    [Fact]
    public void GetStaticDirectoryUsesEnvironmentVariableFallback()
    {
        using var workspace = new TempWorkspace();
        var provider = CreateProvider(environmentStaticPath: workspace.StaticPath);

        var result = provider.GetStaticDirectory();

        Assert.Equal(Path.GetFullPath(workspace.StaticPath), result);
    }

    [Fact]
    public void GetStaticDirectoryUsesOldRootLayoutWhenConfiguredPathIsMissing()
    {
        using var workspace = new TempWorkspace();
        var contentRoot = Path.Combine(workspace.Path, "app", "bin");
        var oldLayout = Path.GetFullPath(Path.Combine(
            contentRoot,
            "..",
            "..",
            "frontend",
            "dist",
            "admin"));
        Directory.CreateDirectory(oldLayout);
        var provider = CreateProvider(contentRootPath: contentRoot);

        var result = provider.GetStaticDirectory();

        Assert.Equal(oldLayout, result);
    }

    [Fact]
    public void GetStaticDirectoryFallsBackToPackagedRootLayout()
    {
        using var workspace = new TempWorkspace();
        var contentRoot = Path.Combine(workspace.Path, "app", "bin", "Debug");
        var expected = Path.GetFullPath(Path.Combine(
            contentRoot,
            "..",
            "..",
            "..",
            "frontend",
            "dist",
            "admin"));
        var provider = CreateProvider(contentRootPath: contentRoot);

        var result = provider.GetStaticDirectory();

        Assert.Equal(expected, result);
    }

    private static AdminStaticDirectoryProvider CreateProvider(
        string? staticPath = null,
        string? environmentStaticPath = null,
        string? contentRootPath = null)
    {
        var values = new Dictionary<string, string?>();
        if (staticPath is not null)
        {
            values["OpenCodex:AdminStaticPath"] = staticPath;
        }

        if (environmentStaticPath is not null)
        {
            values["OPENCODEX_ADMIN_STATIC_PATH"] = environmentStaticPath;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var environment = new FakeHostEnvironment
        {
            ContentRootPath = Path.GetFullPath(contentRootPath ?? Directory.GetCurrentDirectory())
        };

        return new AdminStaticDirectoryProvider(configuration, environment);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "OpenCodex.Api.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencodex-admin-static-{Guid.NewGuid():N}");
            StaticPath = System.IO.Path.Combine(Path, "admin-static");
            Directory.CreateDirectory(StaticPath);
        }

        public string Path { get; }

        public string StaticPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
