using Microsoft.Extensions.FileProviders;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Infrastructure;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexApplicationBuilderExtensions
{
    public static WebApplication UseOpenCodexApi(this WebApplication app)
    {
        OpenCodexDatabaseInitializer.Initialize(app);

        var fileProvider = ResolveStaticFileProvider(app);
        if (fileProvider is not null)
        {
            app.Environment.WebRootFileProvider = fileProvider;
        }

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseMiddleware<ProxyErrorMiddleware>();
        app.Use(async (context, next) =>
        {
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
                string.Equals(context.Request.Path.Value, "/admin", StringComparison.Ordinal))
            {
                context.Request.Path = "/admin/";
            }

            await next();
        });

        if (fileProvider is not null && !app.Environment.IsDevelopment())
        {
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
        }
        else
        {
            app.UseStaticFiles();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapMethods("/admin/", ["GET", "HEAD"], (IWebHostEnvironment environment) =>
        {
            var adminRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
            var indexPath = Path.Combine(adminRoot, "admin", "index.html");
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html")
                : Results.NotFound();
        });
        app.MapMethods(
            "/admin/api/{**path}",
            ["DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT"],
            () => Results.NotFound());
        app.MapFallbackToFile("/admin/{**path:nonfile}", "admin/index.html");

        return app;
    }

    private static IFileProvider? ResolveStaticFileProvider(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            return null;
        }

        var candidates = new List<string?>();
        if (!string.IsNullOrEmpty(app.Environment.WebRootPath))
        {
            candidates.Add(NormalizePath(app.Environment.WebRootPath));
        }
        candidates.Add(NormalizePath(Path.Combine(app.Environment.ContentRootPath, "wwwroot")));
        candidates.Add(NormalizePath(Path.Combine(AppContext.BaseDirectory, "wwwroot")));

        foreach (var candidate in candidates)
        {
            if (candidate is not null && Directory.Exists(candidate))
            {
                return new PhysicalFileProvider(candidate);
            }
        }

        return null;
    }

    // 去掉 Windows 扩展长度路径前缀 \\?\，PhysicalFileProvider 对该前缀处理不一致。
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var p = path;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            p = p[4..];
        }

        return Path.GetFullPath(p);
    }
}
