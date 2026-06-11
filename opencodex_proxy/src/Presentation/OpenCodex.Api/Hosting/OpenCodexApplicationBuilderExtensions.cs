using OpenCodex.Api.Errors;
using OpenCodex.Api.Infrastructure;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexApplicationBuilderExtensions
{
    public static WebApplication UseOpenCodexApi(this WebApplication app)
    {
        OpenCodexDatabaseInitializer.Initialize(app);

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
        app.MapMethods(
            "/admin/api/{**path}",
            ["DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT"],
            () => Results.NotFound());
        app.MapFallbackToFile("/admin/{**path:nonfile}", "admin/index.html");

        return app;
    }
}
