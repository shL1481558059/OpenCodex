using Microsoft.EntityFrameworkCore;

namespace OpenCodex.Data;

public sealed class OpenCodexPostgresDbContext : OpenCodexDbContextBase
{
    public OpenCodexPostgresDbContext(DbContextOptions<OpenCodexPostgresDbContext> options)
        : base(options)
    {
    }
}
