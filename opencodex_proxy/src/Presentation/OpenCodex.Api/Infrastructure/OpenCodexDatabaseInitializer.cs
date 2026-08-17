using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Api.Infrastructure;

public static class OpenCodexDatabaseInitializer
{
    public static void Initialize(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var serviceProvider = scope.ServiceProvider;
        var context = serviceProvider.GetRequiredService<IOpenCodexDbContext>();
        context.Database.Migrate();
    }
}
