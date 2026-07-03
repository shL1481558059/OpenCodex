using Microsoft.Extensions.FileProviders;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Infrastructure;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexApplicationBuilderExtensions
{
    public static WebApplication UseOpenCodexApi(this WebApplication app)
    {
        OpenCodexDatabaseInitializer.Initialize(app);

        // 桌面端单文件发布时，SDK 的 .staticwebassets.endpoints.json 清单不会随 exe 拷贝，
        // 导致默认 WebRootFileProvider 找不到 wwwroot。这里用 PhysicalFileProvider
        // 直接指向物理 wwwroot 目录，保证 UseStaticFiles 和 MapFallbackToFile 正常工作。
        var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (!app.Environment.IsDevelopment() && Directory.Exists(webRoot))
        {
            app.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);
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
        app.UseStaticFiles();
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
}
