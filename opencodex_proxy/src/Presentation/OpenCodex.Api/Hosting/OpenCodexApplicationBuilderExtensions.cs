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
        app.UseSession();
        app.MapControllers();

        return app;
    }
}
