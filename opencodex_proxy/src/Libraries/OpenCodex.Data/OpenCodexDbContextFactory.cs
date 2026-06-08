using Microsoft.EntityFrameworkCore;

namespace OpenCodex.Data;

public static class OpenCodexDbContextFactory
{
    public static OpenCodexDbContext Create(string dbPath)
    {
        var options = new DbContextOptionsBuilder<OpenCodexDbContext>()
            .UseSqlite(ConnectionString(dbPath))
            .Options;
        return new OpenCodexDbContext(options);
    }

    public static string ConnectionString(string dbPath)
    {
        return $"Data Source={Path.GetFullPath(dbPath)}";
    }
}
