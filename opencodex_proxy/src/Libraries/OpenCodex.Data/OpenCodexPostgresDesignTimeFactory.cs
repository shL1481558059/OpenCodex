using Microsoft.EntityFrameworkCore.Design;

namespace OpenCodex.Data;

// 仅供 `dotnet ef migrations` 设计时使用。
public sealed class OpenCodexPostgresDesignTimeFactory : IDesignTimeDbContextFactory<OpenCodexPostgresDbContext>
{
    public OpenCodexPostgresDbContext CreateDbContext(string[] args)
    {
        return OpenCodexDbContextFactory.CreatePostgres(
            "Host=localhost;Port=5432;Database=opencodex_design;Username=postgres;Password=postgres");
    }
}
