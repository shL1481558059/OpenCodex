using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Configuration;

public sealed class AdminStaticDirectoryProvider : IAdminStaticDirectoryProvider
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public AdminStaticDirectoryProvider(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public string GetStaticDirectory()
    {
        var configured = (_configuration["OpenCodex:AdminStaticPath"]
            ?? _configuration["OPENCODEX_ADMIN_STATIC_PATH"]
            ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            return Path.GetFullPath(configured);
        }

        var fromOldRootLayout = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            "..",
            "..",
            "frontend",
            "dist",
            "admin"));
        if (Directory.Exists(fromOldRootLayout))
        {
            return fromOldRootLayout;
        }

        return Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            "..",
            "..",
            "..",
            "frontend",
            "dist",
            "admin"));
    }
}
