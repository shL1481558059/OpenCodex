using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
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
                context.Response.Redirect("/admin/");
                return;
            }

            await next();
        });

        // 显式传入 PhysicalFileProvider，不依赖 WebRootFileProvider 间接传递，
        // 确保单文件发布场景下也能从物理 wwwroot 目录服务静态资源。
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

        // 多候选：优先 WebRootPath，其次 ContentRootPath/wwwroot，最后 BaseDirectory/wwwroot
        var candidates = new List<string?>();
        if (!string.IsNullOrEmpty(app.Environment.WebRootPath))
        {
            candidates.Add(app.Environment.WebRootPath);
        }
        candidates.Add(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        foreach (var candidate in candidates)
        {
            if (candidate is not null && Directory.Exists(candidate))
            {
                return new PhysicalFileProvider(candidate);
            }
        }

        return null;
    }
}
