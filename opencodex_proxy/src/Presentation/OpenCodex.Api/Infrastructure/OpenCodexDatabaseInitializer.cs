using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public static class OpenCodexDatabaseInitializer
{
    public static void Initialize(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var settings = scope.ServiceProvider
            .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
            .GetSettings();
        var directory = Path.GetDirectoryName(Path.GetFullPath(settings.DbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        context.Database.EnsureCreated();
    }
}
