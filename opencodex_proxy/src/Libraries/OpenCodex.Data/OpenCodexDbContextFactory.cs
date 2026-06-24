using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Data;

public static class OpenCodexDbContextFactory
{
    public static IOpenCodexDbContext Create(
        string provider,
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        return NormalizeProvider(provider) switch
        {
            "sqlite" => CreateSqlite(connectionString, migrationsAssembly),
            "postgres" => CreatePostgres(connectionString, migrationsAssembly),
            _ => throw new InvalidOperationException(
                $"Unsupported database provider: '{provider}'. Supported values: sqlite, postgres.")
        };
    }

    public static OpenCodexSqliteDbContext CreateSqlite(
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        var builder = new DbContextOptionsBuilder<OpenCodexSqliteDbContext>();
        ConfigureSqlite(
            builder,
            connectionString,
            migrationsAssembly ?? typeof(OpenCodexSqliteDbContext).Assembly);
        return new OpenCodexSqliteDbContext(builder.Options);
    }

    public static OpenCodexPostgresDbContext CreatePostgres(
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        var builder = new DbContextOptionsBuilder<OpenCodexPostgresDbContext>();
        ConfigurePostgres(
            builder,
            connectionString,
            migrationsAssembly ?? typeof(OpenCodexPostgresDbContext).Assembly);
        return new OpenCodexPostgresDbContext(builder.Options);
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
        switch (NormalizeProvider(provider))
        {
            case "sqlite":
                ConfigureSqlite(
                    builder,
                    connectionString,
                    migrationsAssembly ?? typeof(OpenCodexSqliteDbContext).Assembly);
                return;
            case "postgres":
                ConfigurePostgres(
                    builder,
                    connectionString,
                    migrationsAssembly ?? typeof(OpenCodexPostgresDbContext).Assembly);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{provider}'. Supported values: sqlite, postgres.");
        }
    }

    public static void ConfigureSqlite(
        DbContextOptionsBuilder builder,
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        ConfigureWarnings(builder);
        EnsureSqliteDirectory(connectionString);
        var assemblyName = (migrationsAssembly ?? typeof(OpenCodexSqliteDbContext).Assembly).GetName().Name;
        builder.UseSqlite(
            connectionString,
            sqlite => sqlite.MigrationsAssembly(assemblyName));
    }

    public static void ConfigurePostgres(
        DbContextOptionsBuilder builder,
        string connectionString,
        System.Reflection.Assembly? migrationsAssembly = null)
    {
        ConfigureWarnings(builder);
        var assemblyName = (migrationsAssembly ?? typeof(OpenCodexPostgresDbContext).Assembly).GetName().Name;
        builder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly(assemblyName));
    }

    public static string NormalizeProvider(string provider)
    {
        return (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "sqlite" => "sqlite",
            "postgres" or "postgresql" or "pgsql" => "postgres",
            var value => value
        };
    }

    private static void ConfigureWarnings(DbContextOptionsBuilder builder)
    {
        builder.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
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
