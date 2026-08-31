using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ObservabilityDiagnosticLogFilterTests
{
    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MainLogId = Guid.Parse("33333333-3333-3333-3333-333333334101");
    private static readonly Guid DiagLogId = Guid.Parse("33333333-3333-3333-3333-333333334102");
    private static readonly Guid AttemptLogId = Guid.Parse("33333333-3333-3333-3333-333333334103");
    private static readonly Guid DiagErrorLogId = Guid.Parse("33333333-3333-3333-3333-333333334104");
    private static readonly Guid MainErrorLogId = Guid.Parse("33333333-3333-3333-3333-333333334105");

    [Fact]
    public void DiagnosticLogsExcludedFromStatsLogsAndRecentErrorsByDefault()
    {
        var dbPath = NewDbPath();
        InsertLogs(dbPath);
        var service = CreateService(dbPath);

        var defaultStats = service.ReadStats(
            "custom",
            1_700_000_900,
            1_700_001_100,
            new Dictionary<string, object?>());
        Assert.True(defaultStats.Succeeded);
        Assert.Equal(2, defaultStats.Payload!.Summary.RequestCount);
        Assert.Equal(45, defaultStats.Payload.Summary.TotalTokens);
        Assert.Equal(6.01d, defaultStats.Payload.Summary.Cost, 6);

        var defaultLogs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());
        Assert.True(defaultLogs.Succeeded);
        var defaultIds = defaultLogs.Payload!.Events.Select(item => item.Id).ToHashSet();
        Assert.Equal(2, defaultIds.Count);
        Assert.Contains(MainLogId, defaultIds);
        Assert.Contains(MainErrorLogId, defaultIds);
        Assert.DoesNotContain(DiagLogId, defaultIds);
        Assert.DoesNotContain(DiagErrorLogId, defaultIds);
        Assert.DoesNotContain(AttemptLogId, defaultIds);

        var recentErrors = service.ReadRecentErrors(10);
        Assert.True(recentErrors.Succeeded);
        var recentIds = recentErrors.Payload!.Select(item => item.Id).ToHashSet();
        Assert.Contains(MainErrorLogId, recentIds);
        Assert.DoesNotContain(DiagErrorLogId, recentIds);

        var requestTypeOptions = service.ReadLogFilterOption(
            "request_type",
            null,
            new Dictionary<string, object?>());
        Assert.True(requestTypeOptions.Succeeded);
        var options = Assert.IsType<List<string>>(requestTypeOptions.Payload!["request_types"]);
        Assert.Contains(ProxyRequestTypes.Diagnostic, options);
    }

    [Fact]
    public void DiagnosticLogsReturnedWhenExplicitlyFilteredAndAttemptBehaviorUnchanged()
    {
        var dbPath = NewDbPath();
        InsertLogs(dbPath);
        var service = CreateService(dbPath);

        var diagnosticStats = service.ReadStats(
            "custom",
            1_700_000_900,
            1_700_001_100,
            new Dictionary<string, object?>
            {
                ["request_type"] = ProxyRequestTypes.Diagnostic
            });
        Assert.True(diagnosticStats.Succeeded);
        Assert.Equal(2, diagnosticStats.Payload!.Summary.RequestCount);
        Assert.Equal(450, diagnosticStats.Payload.Summary.TotalTokens);
        Assert.Equal(30d, diagnosticStats.Payload.Summary.Cost, 6);

        var diagnosticLogs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>
        {
            ["request_type"] = ProxyRequestTypes.Diagnostic
        });
        Assert.True(diagnosticLogs.Succeeded);
        var diagnosticIds = diagnosticLogs.Payload!.Events.Select(item => item.Id).ToHashSet();
        Assert.Equal(2, diagnosticIds.Count);
        Assert.Contains(DiagLogId, diagnosticIds);
        Assert.Contains(DiagErrorLogId, diagnosticIds);

        var attemptLogs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>
        {
            ["request_type"] = ProxyRequestTypes.Attempt
        });
        Assert.True(attemptLogs.Succeeded);
        var attempt = Assert.Single(attemptLogs.Payload!.Events);
        Assert.Equal(AttemptLogId, attempt.Id);
    }

    private static ObservabilityService CreateService(string dbPath)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
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

    private static void InsertLogs(string dbPath)
    {
        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
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
        context.RequestLogs.AddRange(
            new RequestLog
            {
                Id = MainLogId,
                RequestId = "req-main",
                CreatedAt = 1_700_001_000,
                Method = "POST",
                Path = "/v1/responses",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                RequestType = ProxyRequestTypes.Main,
                LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                IsStream = false,
                StatusCode = 200,
                OwnerUserId = AdminUserId,
                InputTokens = 10,
                OutputTokens = 5,
                Cost = 0.01
            },
            new RequestLog
            {
                Id = DiagLogId,
                RequestId = "req-diag",
                CreatedAt = 1_700_001_010,
                Method = "POST",
                Path = "/test-channel/stream",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                RequestType = ProxyRequestTypes.Diagnostic,
                LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                IsStream = true,
                StatusCode = 200,
                OwnerUserId = AdminUserId,
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 10
            },
            new RequestLog
            {
                Id = AttemptLogId,
                RequestId = "req-attempt",
                CreatedAt = 1_700_001_020,
                Method = "POST",
                Path = "/v1/responses",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                RequestType = ProxyRequestTypes.Attempt,
                ParentRequestLogId = MainLogId,
                LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                IsStream = false,
                StatusCode = 502,
                Error = "primary unavailable",
                OwnerUserId = AdminUserId,
                InputTokens = 1000,
                OutputTokens = 500,
                Cost = 100
            },
            new RequestLog
            {
                Id = DiagErrorLogId,
                RequestId = "req-diag-error",
                CreatedAt = 1_700_001_030,
                Method = "POST",
                Path = "/test-channel/stream",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                RequestType = ProxyRequestTypes.Diagnostic,
                LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                IsStream = true,
                StatusCode = 502,
                Error = "diag upstream failed",
                OwnerUserId = AdminUserId,
                InputTokens = 200,
                OutputTokens = 100,
                Cost = 20
            },
            new RequestLog
            {
                Id = MainErrorLogId,
                RequestId = "req-main-error",
                CreatedAt = 1_700_001_040,
                Method = "POST",
                Path = "/v1/responses",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                RequestType = ProxyRequestTypes.Main,
                LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                IsStream = false,
                StatusCode = 500,
                Error = "main upstream failed",
                OwnerUserId = AdminUserId,
                InputTokens = 20,
                OutputTokens = 10,
                Cost = 6
            });
        context.SaveChanges();
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
                : throw new UnauthorizedAccessException("superadmin required");
        }
    }
}
