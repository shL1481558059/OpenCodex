using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ObservabilityServiceTests
{
    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid KeyId101 = Guid.Parse("22222222-2222-2222-2222-222222222201");
    private static readonly Guid ChannelId401 = Guid.Parse("44444444-4444-4444-4444-444444444401");

    private static ObservabilityService CreateService(
        string dbPath,
        IChannelCapacityService? channelCapacity = null)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ObservabilityService(
            new TestSettingsProvider(dbPath),
            new TestWorkContext(AdminUserId, "admin", "superadmin"),
            new EfRepository<RequestLog>(context),
            new EfRepository<RequestLogDetail>(context),
            new EfRepository<RequestLogStreamLine>(context),
            new EfRepository<AccessApiKey>(context),
            new EfRepository<User>(context),
            new EfRepository<Channel>(context),
            channelCapacity ?? new ChannelCapacityService());
    }

    [Fact]
    public void LogsAndApiKeyFilterOptionsExposeApiKeyName()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
            context.AccessApiKeys.Add(new AccessApiKey
            {
                Id = KeyId101,
                OwnerUserId = AdminUserId,
                Name = "Primary Key",
                KeyHash = "hash-101",
                KeyPrefix = "sk",
                KeySuffix = "0101",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.Channels.Add(new Channel
            {
                Id = ChannelId401,
                OwnerUserId = AdminUserId,
                Position = 0,
                Name = "主渠道",
                Type = "openai",
                BaseUrl = "https://example.com",
                ApiKey = "sk-channel",
                AuthMode = "config",
                HeadersJson = "{}",
                TimeoutSeconds = 30,
                RetryCount = 0,
                Capacity = 1,
                CompatJson = "{}",
                ModelsJson = "[]",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.RequestLogs.Add(new RequestLog
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333301"),
                RequestId = "req-501",
                CreatedAt = 1,
                Method = "POST",
                Path = "/v1/chat/completions",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                ChannelId = ChannelId401,
                IsStream = false,
                StatusCode = 200,
                OwnerUserId = AdminUserId,
                ApiKeyId = KeyId101
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        var logs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());

        Assert.True(logs.Succeeded);
        var log = Assert.Single(logs.Payload!.Events);
        Assert.Equal(KeyId101, log.ApiKeyId);
        Assert.Equal("Primary Key", log.ApiKeyName);
        Assert.Equal(ChannelId401.ToString(), log.ChannelId);
        Assert.Equal("主渠道", log.ChannelName);

        var options = service.ReadLogFilterOption(
            "api_key_id",
            "Primary",
            new Dictionary<string, object?>());

        Assert.True(options.Succeeded);
        var apiKeyOptions = Assert.IsType<List<LogApiKeyFilterOption>>(options.Payload!["api_key_ids"]);
        var option = Assert.Single(apiKeyOptions);
        Assert.Equal(KeyId101, option.Id);
        Assert.Equal("Primary Key", option.Name);
    }

    [Fact]
    public void StatsRespectsLogFilters()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333311"),
                    RequestId = "req-a",
                    CreatedAt = 1_700_000_000,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-4.1",
                    UpstreamModel = "gpt-4.1",
                    ChannelId = Guid.Parse("44444444-4444-4444-4444-444444444411"),
                    IsStream = true,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    InputTokens = 120,
                    CachedTokens = 30,
                    OutputTokens = 50,
                    Cost = 3.5
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333312"),
                    RequestId = "req-b",
                    CreatedAt = 1_700_000_030,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-4.1",
                    UpstreamModel = "gpt-4.1",
                    ChannelId = Guid.Parse("44444444-4444-4444-4444-444444444412"),
                    IsStream = false,
                    StatusCode = 500,
                    Error = "upstream failed",
                    OwnerUserId = AdminUserId,
                    InputTokens = 80,
                    CachedTokens = 0,
                    OutputTokens = 20,
                    Cost = 1.2
                });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        var stats = service.ReadStats(
            "custom",
            1_699_999_900,
            1_700_000_120,
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-4.1",
                ["channel_id"] = Guid.Parse("44444444-4444-4444-4444-444444444411").ToString(),
                ["request_status"] = "success"
            });

        Assert.True(stats.Succeeded);
        var summary = stats.Payload!.Summary;
        Assert.Equal(1, summary.RequestCount);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(200, summary.TotalTokens);
        Assert.Equal(3.5d, summary.Cost, 6);

        var point = Assert.Single(
            stats.Payload.Points,
            item => item.InputTokens > 0 || item.CachedTokens > 0 || item.OutputTokens > 0);
        Assert.Equal(120L, point.InputTokens);
        Assert.Equal(30L, point.CachedTokens);
        Assert.Equal(50L, point.OutputTokens);

        var model = Assert.Single(stats.Payload.ModelDistribution);
        Assert.Equal("gpt-4.1", model.Model);
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void LogsAndStatsExcludeAttemptLogsByDefault()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var mainLogId = Guid.Parse("33333333-3333-3333-3333-333333333351");
        var attemptLogId = Guid.Parse("33333333-3333-3333-3333-333333333352");

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
                    Id = mainLogId,
                    RequestId = "req-attempt-filter",
                    CreatedAt = 1_700_001_000,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
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
                    Id = attemptLogId,
                    RequestId = "req-attempt-filter",
                    CreatedAt = 1_700_001_001,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    RequestType = ProxyRequestTypes.Attempt,
                    ParentRequestLogId = mainLogId,
                    LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                    IsStream = false,
                    StatusCode = 502,
                    Error = "primary unavailable",
                    OwnerUserId = AdminUserId,
                    InputTokens = 999,
                    OutputTokens = 999,
                    Cost = 99
                });
            context.RequestLogDetails.Add(new RequestLogDetail
            {
                RequestLogId = attemptLogId,
                ResponseBody = "{\"route_attempt_number\":1,\"route_retry_number\":0}",
                UpstreamResponseBody = "{\"error\":{\"type\":\"rate_limit_exceeded\",\"message\":\"primary unavailable\"}}"
            });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        var defaultLogs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());
        Assert.True(defaultLogs.Succeeded);
        var defaultLog = Assert.Single(defaultLogs.Payload!.Events);
        Assert.Equal(mainLogId, defaultLog.Id);
        Assert.Equal("success", defaultLog.RequestStatus);
        Assert.Equal("success_with_retry", defaultLog.DisplayRequestStatus);
        Assert.Equal(1, defaultLog.AttemptCount);
        Assert.Equal(1, defaultLog.FailedAttemptCount);

        var defaultStats = service.ReadStats(
            "custom",
            1_700_000_900,
            1_700_001_100,
            new Dictionary<string, object?>());
        Assert.True(defaultStats.Succeeded);
        Assert.Equal(1, defaultStats.Payload!.Summary.RequestCount);
        Assert.Equal(15, defaultStats.Payload.Summary.TotalTokens);

        var attemptLogs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>
        {
            ["request_type"] = ProxyRequestTypes.Attempt
        });
        Assert.True(attemptLogs.Succeeded);
        var attemptLog = Assert.Single(attemptLogs.Payload!.Events);
        Assert.Equal(attemptLogId, attemptLog.Id);
        Assert.Equal(mainLogId, attemptLog.ParentRequestLogId);

        var attemptStats = service.ReadStats(
            "custom",
            1_700_000_900,
            1_700_001_100,
            new Dictionary<string, object?>
            {
                ["request_type"] = ProxyRequestTypes.Attempt
            });
        Assert.True(attemptStats.Succeeded);
        Assert.Equal(1, attemptStats.Payload!.Summary.RequestCount);
        Assert.Equal(1_998, attemptStats.Payload.Summary.TotalTokens);

        var detail = service.ReadLogById(attemptLogId);
        Assert.True(detail.Succeeded);
        Assert.Equal(ProxyRequestTypes.Attempt, detail.Payload!.RequestType);
        Assert.Contains("\"route_attempt_number\":1", detail.Payload.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("\"rate_limit_exceeded\"", detail.Payload.UpstreamResponseBody, StringComparison.Ordinal);

        var requestTypeOptions = service.ReadLogFilterOption(
            "request_type",
            null,
            new Dictionary<string, object?>());
        Assert.True(requestTypeOptions.Succeeded);
        var options = Assert.IsType<List<string>>(requestTypeOptions.Payload!["request_types"]);
        Assert.Contains(ProxyRequestTypes.Attempt, options);
    }

    [Fact]
    public void StatsUsesCurrentTimeForRecentMetricsWhenCustomEndIsInFuture()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333321"),
                    RequestId = "req-recent",
                    CreatedAt = now - 30,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    IsStream = true,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    InputTokens = 10,
                    OutputTokens = 5,
                    Cost = 0.01
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333322"),
                    RequestId = "req-old",
                    CreatedAt = now - 7200,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    IsStream = true,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId,
                    InputTokens = 20,
                    OutputTokens = 10,
                    Cost = 0.02
                });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        var stats = service.ReadStats(
            "custom",
            now - 10800,
            now + 10800,
            new Dictionary<string, object?>());

        Assert.True(stats.Succeeded);
        var summary = stats.Payload!.Summary;
        Assert.Equal(2, summary.RequestCount);
        Assert.Equal(1, summary.Recent1hRequestCount);
        Assert.Equal(15, summary.Recent1hTokens);
        Assert.True(summary.Rpm > 0);
        Assert.True(summary.Tpm > 0);
    }

    [Fact]
    public void ActiveChannelQueue_UsesRuntimeCapacityInsteadOfProcessingLogs()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var channelId402 = Guid.Parse("44444444-4444-4444-4444-444444444402");

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
            context.Channels.AddRange(
                new Channel
                {
                    Id = ChannelId401,
                    OwnerUserId = AdminUserId,
                    Position = 0,
                    Name = "日志残留渠道",
                    Type = "openai",
                    BaseUrl = "https://example.com/a",
                    ApiKey = "sk-a",
                    AuthMode = "config",
                    HeadersJson = "{}",
                    TimeoutSeconds = 30,
                    RetryCount = 0,
                    Capacity = 3,
                    CompatJson = "{}",
                    ModelsJson = "[]",
                    Enabled = true,
                    CreatedAt = 1,
                    UpdatedAt = 1
                },
                new Channel
                {
                    Id = channelId402,
                    OwnerUserId = AdminUserId,
                    Position = 1,
                    Name = "实时占用渠道",
                    Type = "openai",
                    BaseUrl = "https://example.com/b",
                    ApiKey = "sk-b",
                    AuthMode = "config",
                    HeadersJson = "{}",
                    TimeoutSeconds = 30,
                    RetryCount = 0,
                    Capacity = 3,
                    CompatJson = "{}",
                    ModelsJson = "[]",
                    Enabled = true,
                    CreatedAt = 1,
                    UpdatedAt = 1
                });
            context.RequestLogs.Add(new RequestLog
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333341"),
                RequestId = "req-stale-processing",
                CreatedAt = 1_700_000_100,
                Method = "POST",
                Path = "/v1/responses",
                Model = "gpt-test",
                ChannelId = ChannelId401,
                LifecycleStatus = ProxyRequestLifecycleStatus.Processing,
                IsStream = true,
                OwnerUserId = AdminUserId
            });
            context.SaveChanges();
        }

        var capacity = new ChannelCapacityService();
        var runtimeChannel = new Dictionary<string, object?>
        {
            ["id"] = channelId402.ToString(),
            ["capacity"] = 3
        };
        using var lease1 = capacity.TryAcquire("admin", runtimeChannel);
        using var lease2 = capacity.TryAcquire("admin", runtimeChannel);

        var service = CreateService(dbPath, capacity);

        var queue = service.ReadActiveChannelQueue();

        Assert.True(queue.Succeeded);
        var item = Assert.Single(queue.Payload!.Channels);
        Assert.Equal(channelId402.ToString(), item.ChannelId);
        Assert.Equal("实时占用渠道", item.ChannelName);
        Assert.Equal(2, item.ProcessingCount);
    }

    [Fact]
    public void LogsAndDetailsExposeLifecycleStatusesAndStreamLines()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
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
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333331"),
                    RequestId = "req-queued",
                    CreatedAt = 1_700_000_000,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Queued,
                    IsStream = true,
                    OwnerUserId = AdminUserId
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333332"),
                    RequestId = "req-processing",
                    CreatedAt = 1_700_000_010,
                    ProcessingStartedAt = 1_700_000_011,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Processing,
                    IsStream = true,
                    OwnerUserId = AdminUserId
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    RequestId = "req-success",
                    CreatedAt = 1_700_000_020,
                    ProcessingStartedAt = 1_700_000_021,
                    CompletedAt = 1_700_000_022,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Success,
                    IsStream = true,
                    StatusCode = 200,
                    OwnerUserId = AdminUserId
                },
                new RequestLog
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333334"),
                    RequestId = "req-failed",
                    CreatedAt = 1_700_000_030,
                    ProcessingStartedAt = 1_700_000_031,
                    CompletedAt = 1_700_000_032,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Failed,
                    IsStream = true,
                    StatusCode = 500,
                    Error = "failed",
                    OwnerUserId = AdminUserId
                });
            context.RequestLogDetails.Add(new RequestLogDetail
            {
                RequestLogId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                RequestBody = "{\"model\":\"gpt-test\"}"
            });
            context.RequestLogStreamLines.AddRange(
                new RequestLogStreamLine
                {
                    RequestLogId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Sequence = 0,
                    OccurredAt = 1_700_000_020.100,
                    Source = "upstream",
                    RawLine = "event: response.output_text.delta"
                },
                new RequestLogStreamLine
                {
                    RequestLogId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Sequence = 1,
                    OccurredAt = 1_700_000_020.120,
                    Source = "upstream",
                    RawLine = "data: {\"delta\":\"hi\"}"
                });
            context.SaveChanges();
        }

        var service = CreateService(dbPath);

        var logs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());

        Assert.True(logs.Succeeded);
        Assert.Equal(
            new[]
            {
                ProxyRequestLifecycleStatus.Failed,
                ProxyRequestLifecycleStatus.Success,
                ProxyRequestLifecycleStatus.Processing,
                ProxyRequestLifecycleStatus.Queued
            },
            logs.Payload!.Events.Select(item => item.RequestStatus).ToArray());

        var processingOnly = service.ReadLogsPage(1, 20, new Dictionary<string, object?>
        {
            ["request_status"] = ProxyRequestLifecycleStatus.Processing
        });
        Assert.True(processingOnly.Succeeded);
        var processingLog = Assert.Single(processingOnly.Payload!.Events);
        Assert.Equal("req-processing", processingLog.RequestId);

        var detail = service.ReadLogById(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        Assert.True(detail.Succeeded);
        Assert.Equal(ProxyRequestLifecycleStatus.Success, detail.Payload!.RequestStatus);
        Assert.NotNull(detail.Payload.StreamLines);
        Assert.Collection(
            detail.Payload.StreamLines!,
            line =>
            {
                Assert.Equal(0, line.Sequence);
                Assert.Equal("upstream", line.Source);
                Assert.Equal("event: response.output_text.delta", line.RawLine);
            },
            line =>
            {
                Assert.Equal(1, line.Sequence);
                Assert.Equal("data: {\"delta\":\"hi\"}", line.RawLine);
            });
    }

    private sealed class TestSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public TestSettingsProvider(string dbPath)
        {
            _settings = new OpenCodexRuntimeSettings(
                "sqlite",
                $"Data Source={dbPath}",
                "admin",
                "password",
                120);
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
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
