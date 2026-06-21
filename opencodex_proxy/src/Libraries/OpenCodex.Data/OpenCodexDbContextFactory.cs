using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OpenCodex.Data;

public static class OpenCodexDbContextFactory
{
    public static OpenCodexDbContext Create(string provider, string connectionString, System.Reflection.Assembly? migrationsAssembly = null)
    {
        var builder = new DbContextOptionsBuilder<OpenCodexDbContext>();
        builder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        ConfigureProvider(builder, provider, connectionString, migrationsAssembly ?? typeof(OpenCodexDbContext).Assembly);
        return new OpenCodexDbContext(builder.Options);
    }

    /// <summary>
    /// 在指定的 <see cref="DbContextOptionsBuilder"/> 上按 provider 配置连接,供 DI 注册和测试复用。
    /// </summary>
    public static void Configure(
        DbContextOptionsBuilder builder,
        string provider,
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        builder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        ConfigureProvider(builder, provider, connectionString, migrationsAssembly ?? typeof(OpenCodexDbContext).Assembly);
    }

    private static void ConfigureProvider(
        DbContextOptionsBuilder builder,
        string provider,
        string connectionString,
        System.Reflection.Assembly migrationsAssembly)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var assemblyName = migrationsAssembly.GetName().Name;
        switch (normalizedProvider)
        {
            case "sqlite":
                EnsureSqliteDirectory(connectionString);
                builder.UseSqlite(
                    connectionString,
                    sqlite => sqlite.MigrationsAssembly(assemblyName));
                return;
            case "postgres":
            case "postgresql":
            case "pgsql":
                builder.UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsAssembly(assemblyName));
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{provider}'. Supported values: sqlite, postgres.");
        }
    }

    private static void EnsureSqliteDirectory(string connectionString)
    {
        // SQLite 连接串形如 "Data Source=logs/opencodex.db";确保目录存在,否则 Migrate 会失败。
        var path = ExtractSqliteDataSource(connectionString);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string? ExtractSqliteDataSource(string connectionString)
    {
        // 简单解析 "Data Source=..." 形式,不引入完整连接串解析器。
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var span = part.AsSpan().Trim();
            var equals = span.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var key = span[..equals].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DataSource", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                return span[(equals + 1)..].Trim().ToString();
            }
        }
        return null;
    }
}
