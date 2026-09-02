using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 针对统计聚合的 SQL 语义验收：分桶边界、跨 provider 翻译、摘要合并后的 SQL 条数。
/// </summary>
public sealed class ObservabilityAggregationSqlTests
{
    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void TimeseriesBucketBoundary_RoundsDownToBucket1()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.CreateSqlite($"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            SeedUser(context);

        // 起始时间对齐，桶宽 60 秒，日志恰落在偏移 90 秒处：
        // 90 / 60 = 1.5，必须向下取整进桶 1（索引 1），而不是进桶 2。
        var start = 1_700_000_000;
        var end = start + 240;
            context.RequestLogs.Add(new RequestLog
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333335001"),
                RequestId = "req-boundary",
                CreatedAt = start + 90,
                RequestType = ProxyRequestTypes.Main,
                LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                StatusCode = 200,
                OwnerUserId = AdminUserId,
                TtftMs = 100,
                InputTokens = 1,
                CachedTokens = 0,
                OutputTokens = 1,
                Cost = 0.01
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var stats = service.ReadStats(
            "custom",
            1_700_000_000,
            1_700_000_240,
            new Dictionary<string, object?>());

        Assert.True(stats.Succeeded);
        // 240 秒跨度、60 秒桶宽 -> 4 个点，日志落在索引 1 的桶。
        var points = stats.Payload!.Points;
        Assert.Equal(4, points.Count);
        var nonZero = Assert.Single(points, item => item.InputTokens > 0);
        Assert.Equal(points[1].Time, nonZero.Time);
        Assert.Equal(1, nonZero.InputTokens);
        Assert.Equal(1, nonZero.OutputTokens);
    }

    [Fact]
    public void PostgresBucketQuery_TranslatesFloorAndDoesNotCastToBigint()
    {
        using var context = OpenCodexDbContextFactory.CreatePostgres(
            "Host=localhost;Database=none;Username=none;Password=none");
        const double startTs = 1_700_000_000;
        const double bucketSeconds = 60.0;

        var sql = context.RequestLogs
            .Where(log => log.CreatedAt >= startTs && log.CreatedAt < startTs + 3600)
            .GroupBy(log => Math.Floor((log.CreatedAt!.Value - startTs) / bucketSeconds))
            .Select(group => new
            {
                Bucket = group.Key,
                Count = group.Count(),
                Cost = group.Sum(log => log.Cost)
            })
            .ToQueryString();

        // 向下取整必须翻译成 floor(...)，不允许退化成 float->bigint 的四舍五入强转。
        Assert.Contains("floor(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CAST(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("::bigint", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadStatsSummary_EmptyTableReturnsAllZero()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.CreateSqlite($"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            SeedUser(context);
        }

        var interceptor = new AggregationSqlProbe();
        using var captureContext = AggregationSqlProbe.CreateCapturingContext(dbPath, interceptor);
        var service = CreateService(dbPath, injectedContext: captureContext);
        interceptor.Reset();

        var summary = service.ReadStatsSummary(
            "custom",
            1_700_000_000,
            1_700_000_100,
            new Dictionary<string, object?>());

        Assert.True(summary.Succeeded);
        Assert.Equal(0, summary.Payload!.RequestCount);
        Assert.Equal(0, summary.Payload.SuccessCount);
        Assert.Equal(0, summary.Payload.Recent1hRequestCount);
        Assert.Equal(0, summary.Payload.InputTokens);
        Assert.Equal(0, summary.Payload.CachedTokens);
        Assert.Equal(0, summary.Payload.OutputTokens);
        Assert.Equal(0, summary.Payload.TotalTokens);
        Assert.Equal(0, summary.Payload.Recent1hTokens);
        Assert.Equal(0, summary.Payload.Cost);
        Assert.Equal(0, summary.Payload.Recent1hCost);
        Assert.Equal(0, summary.Payload.Rpm);
        Assert.Equal(0, summary.Payload.Tpm);
        // 空表 GroupBy(_ => 1) 返回 0 行并直接回退全 0，不执行 successCount 那条，
        // 所以只有 1 条条件聚合查询。
        Assert.Equal(1, interceptor.SelectCount);
    }

    [Fact]
    public void ReadStatsSummary_NonEmptyTableAggregatesInTwoSql()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.CreateSqlite($"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            SeedUser(context);
            context.RequestLogs.AddRange(
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333335021"),
                    RequestId = "req-summary-ok",
                    CreatedAt = 1_700_000_010,
                    RequestType = ProxyRequestTypes.Main,
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    TtftMs = 40,
                    InputTokens = 10,
                    CachedTokens = 2,
                    OutputTokens = 5,
                    Cost = 0.5
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333335022"),
                    RequestId = "req-summary-fail",
                    CreatedAt = 1_700_000_011,
                    RequestType = ProxyRequestTypes.Main,
                    LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                    StatusCode = 500,
                    Error = "boom",
                    OwnerUserId = AdminUserId,
                    InputTokens = 20,
                    OutputTokens = 8,
                    Cost = 1.5
                });
            context.SaveChanges();
        }

        var interceptor = new AggregationSqlProbe();
        using var captureContext = AggregationSqlProbe.CreateCapturingContext(dbPath, interceptor);
        var service = CreateService(dbPath, injectedContext: captureContext);
        interceptor.Reset();

        var summary = service.ReadStatsSummary(
            "custom",
            1_700_000_000,
            1_700_000_100,
            new Dictionary<string, object?>());

        Assert.True(summary.Succeeded);
        Assert.Equal(2, summary.Payload!.RequestCount);
        Assert.Equal(1, summary.Payload.SuccessCount);
        Assert.Equal(30, summary.Payload.InputTokens);
        Assert.Equal(2, summary.Payload.CachedTokens);
        Assert.Equal(13, summary.Payload.OutputTokens);
        Assert.Equal(43, summary.Payload.TotalTokens);
        Assert.Equal(2.0, summary.Payload.Cost, 6);
        // 主聚合、成功数聚合和两种币种成本聚合各执行 1 条 SQL。
        Assert.Equal(4, interceptor.SelectCount);
    }

    [Fact]
    public void Timeseries_PadsEmptyBucketsAndTtftOnlyCountsPositive()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.CreateSqlite($"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            SeedUser(context);
            var start = 1_700_000_000;
            context.RequestLogs.AddRange(
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333335011"),
                    RequestId = "req-ttft-positive",
                    CreatedAt = start,
                    RequestType = ProxyRequestTypes.Main,
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    TtftMs = 50,
                    InputTokens = 1,
                    OutputTokens = 1,
                    Cost = 0.01
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333335012"),
                    RequestId = "req-ttft-zero",
                    CreatedAt = start + 1,
                    RequestType = ProxyRequestTypes.Main,
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    TtftMs = 0,
                    InputTokens = 1,
                    OutputTokens = 1,
                    Cost = 0.01
                });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);
        var stats = service.ReadStats(
            "custom",
            1_700_000_000,
            1_700_000_120,
            new Dictionary<string, object?>());

        Assert.True(stats.Succeeded);
        // 2 分钟跨度、1 分钟桶宽 -> 2 个点，桶 1 为空桶补零。
        var points = stats.Payload!.Points;
        Assert.True(
            points.Count == 2,
            "points=" + string.Join(",", points.Select(p => $"{p.Time}#{p.Cost}")));
        Assert.Equal(0, points[1].Cost);
        Assert.Equal(0, points[1].Rpm);
        Assert.Null(points[1].AvgTtftMs);

        var bucket = points[0];
        Assert.Equal(2, bucket.InputTokens);
        Assert.Equal(2, bucket.OutputTokens);
        Assert.Equal(50, bucket.AvgTtftMs);
    }

    private static ObservabilityService CreateService(
        string dbPath,
        IOpenCodexDbContext? injectedContext = null)
    {
        var context = injectedContext ?? OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ObservabilityService(
            new TestWorkContext(AdminUserId, "admin", "superadmin"),
            context,
            new EfRepository<RequestLog>(context),
            new EfRepository<AccessApiKey>(context),
            new EfRepository<User>(context),
            new EfRepository<Channel>(context),
            new EfRepository<RequestLogContentRef>(context),
            new EfRepository<LogContentManifestChunk>(context),
            new EfRepository<LogContentManifest>(context),
            new EfRepository<LogContentBlock>(context),
            new ChannelCapacityService(),
            new ProxySettingsService(new EfRepository<ProxySetting>(context)),
            new ServiceCollection().AddMemoryCache().BuildServiceProvider().GetRequiredService<IMemoryCache>());
    }

    private static string NewDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }

    private static void SeedUser(OpenCodexSqliteDbContext context)
    {
        context.Users.Add(new User
        {
            Id = AdminUserId,
            Username = "admin",
            PasswordHash = "hash",
            Role = "superadmin",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
    }

    private sealed class TestWorkContext : IWorkContext
    {
        private readonly SessionUser _user;

        public TestWorkContext(Guid userId, string username, string role)
        {
            _user = new SessionUser(userId, username, role, true);
        }

        public SessionUser? CurrentUser => _user;

        public bool IsSignedIn => true;

        public bool IsSuperadmin => _user.Role == "superadmin";

        public SessionUser RequireUser()
        {
            return _user;
        }

        public SessionUser RequireSuperadmin()
        {
            return IsSuperadmin
                ? _user
                : throw new InvalidOperationException("superadmin required");
        }
    }

    /// <summary>
    /// 本测试文件私有的 SQL 捕获拦截器，统计 SELECT 条数；不依赖他人正在改的
    /// Infrastructure/SqlCapture。
    /// </summary>
    private sealed class AggregationSqlProbe : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_sync)
                {
                    return _commands.ToArray();
                }
            }
        }

        public int SelectCount =>
            Commands.Count(command =>
                new string(command.Where(character => !char.IsWhiteSpace(character)).Take(6).ToArray())
                    .Equals("SELECT", StringComparison.OrdinalIgnoreCase));

        public void Reset()
        {
            lock (_sync)
            {
                _commands.Clear();
            }
        }

        public static OpenCodexSqliteDbContext CreateCapturingContext(
            string dbPath,
            AggregationSqlProbe probe)
        {
            var builder = new DbContextOptionsBuilder<OpenCodexSqliteDbContext>();
            OpenCodexDbContextFactory.ConfigureSqlite(builder, $"Data Source={dbPath}");
            builder.AddInterceptors(probe);
            return new OpenCodexSqliteDbContext(builder.Options);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command.CommandText);
            return result;
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Capture(command.CommandText);
            return result;
        }

        private void Capture(string commandText)
        {
            lock (_sync)
            {
                _commands.Add(commandText);
            }
        }
    }
}
