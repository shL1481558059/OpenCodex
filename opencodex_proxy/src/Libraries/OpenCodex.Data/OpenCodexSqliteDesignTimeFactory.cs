using Microsoft.EntityFrameworkCore.Design;

namespace OpenCodex.Data;

// 仅供 `dotnet ef migrations` 设计时使用。
public sealed class OpenCodexSqliteDesignTimeFactory : IDesignTimeDbContextFactory<OpenCodexSqliteDbContext>
{
    public OpenCodexSqliteDbContext CreateDbContext(string[] args)
    {
        return OpenCodexDbContextFactory.CreateSqlite("Data Source=design-time.db");
    }
}
