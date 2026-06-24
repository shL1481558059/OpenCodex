using Microsoft.EntityFrameworkCore;

namespace OpenCodex.Data;

public sealed class OpenCodexSqliteDbContext : OpenCodexDbContextBase
{
    public OpenCodexSqliteDbContext(DbContextOptions<OpenCodexSqliteDbContext> options)
        : base(options)
    {
    }
}
