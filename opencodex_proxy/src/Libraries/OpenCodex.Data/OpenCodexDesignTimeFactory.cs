using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenCodex.Data;

// 仅供 `dotnet ef migrations` 设计时使用,运行时由 OpenCodexDbContextFactory.Create 提供 DbContext。
public sealed class OpenCodexDesignTimeFactory : IDesignTimeDbContextFactory<OpenCodexDbContext>
{
    public OpenCodexDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpenCodexDbContext>()
            .UseSqlite("Data Source=design-time.db", sqlite => sqlite.MigrationsAssembly(typeof(OpenCodexDbContext).Assembly.GetName().Name))
            .Options;
        return new OpenCodexDbContext(options);
    }
}
