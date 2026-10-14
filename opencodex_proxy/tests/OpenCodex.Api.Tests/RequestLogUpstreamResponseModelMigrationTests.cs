using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenCodex.Core.Domain;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RequestLogUpstreamResponseModelMigrationTests
{
    private const string PreviousMigration = "RequestLogPricingPhase";
    private const string ResponseModelMigration = "RequestLogUpstreamResponseModel";

    [Fact]
    public void Sqlite_MigrationAddsNullableUpstreamResponseModelWithoutTouchingHistoricalRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-api-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var context = OpenCodexDbContextFactory.CreateSqlite($"Data Source={dbPath}");
        var migrator = context.GetService<IMigrator>();
        migrator.Migrate(PreviousMigration);
        context.Database.ExecuteSqlRaw(
            "INSERT INTO \"RequestLogs\" (\"Id\", \"RequestType\", \"IsStream\", \"InputTokens\", \"CachedTokens\", "
            + "\"CacheWriteTokens\", \"CacheReadTokens\", \"OutputTokens\", \"Cost\", \"CostCurrency\", \"OwnerUserId\") "
            + "VALUES ('33333333-3333-3333-3333-333333333392', 'main', 0, 0, 0, 0, 0, 0, 0, 'USD', "
            + "'11111111-1111-1111-1111-111111111111')");

        context.Database.Migrate();

        Assert.Empty(context.Database.GetPendingMigrations());
        var historical = context.RequestLogs.AsNoTracking().Single();
        Assert.Null(historical.UpstreamResponseModel);
        var column = context.Database
            .SqlQueryRaw<SqliteColumn>(
                "SELECT \"name\" AS \"Name\", \"type\" AS \"Type\", \"notnull\" AS \"NotNull\" "
                + "FROM pragma_table_info('RequestLogs') WHERE \"name\" = 'UpstreamResponseModel'")
            .AsEnumerable()
            .Single();
        Assert.Equal("TEXT", column.Type);
        Assert.Equal(0, column.NotNull);
        var indexes = context.Database
            .SqlQueryRaw<string>(
                "SELECT \"sql\" AS \"Value\" FROM sqlite_master "
                + "WHERE \"type\" = 'index' AND \"tbl_name\" = 'RequestLogs' AND \"sql\" LIKE '%UpstreamResponseModel%'")
            .ToList();
        Assert.Empty(indexes);
    }

    [Fact]
    public void Postgres_MigrationScriptAddsNullableUpstreamResponseModelColumn()
    {
        using var context = OpenCodexDbContextFactory.CreatePostgres(
            "Host=localhost;Database=none;Username=none;Password=none");
        var script = context.GetService<IMigrator>().GenerateScript(PreviousMigration, ResponseModelMigration);

        Assert.Contains(
            "ALTER TABLE \"RequestLogs\" ADD \"UpstreamResponseModel\" text;",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE \"RequestLogs\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void ModelSnapshot_MatchesModelForBothProviders(string provider)
    {
        using var context = provider == "sqlite"
            ? (DbContext)OpenCodexDbContextFactory.CreateSqlite("Data Source=:memory:")
            : OpenCodexDbContextFactory.CreatePostgres("Host=localhost;Database=none;Username=none;Password=none");

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains(
            context.Database.GetMigrations(),
            id => id.EndsWith("_" + ResponseModelMigration, StringComparison.Ordinal));
        var property = context.Model.FindEntityType(typeof(RequestLog))!
            .FindProperty(nameof(RequestLog.UpstreamResponseModel))!;
        Assert.True(property.IsNullable);
        Assert.Null(property.GetMaxLength());
    }

    private sealed class SqliteColumn
    {
        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public long NotNull { get; set; }
    }
}
