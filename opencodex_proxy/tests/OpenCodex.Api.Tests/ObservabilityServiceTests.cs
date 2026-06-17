using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ObservabilityServiceTests
{
    [Fact]
    public void LogsAndApiKeyFilterOptionsExposeApiKeyName()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            context.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = "hash",
                Role = "superadmin",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.AccessApiKeys.Add(new AccessApiKey
            {
                Id = 101,
                OwnerUsername = "admin",
                Name = "Primary Key",
                KeyHash = "hash-101",
                KeyPrefix = "sk",
                KeySuffix = "0101",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.RequestLogs.Add(new RequestLog
            {
                Id = 501,
                RequestId = "req-501",
                CreatedAt = 1,
                Method = "POST",
                Path = "/v1/chat/completions",
                Model = "gpt-test",
                UpstreamModel = "gpt-test",
                ChannelId = "channel-a",
                IsStream = false,
                StatusCode = 200,
                OwnerUsername = "admin",
                ApiKeyId = 101
            });
            context.SaveChanges();
        }

        var service = new ObservabilityService(
            new TestSettingsProvider(dbPath),
            new TestWorkContext("admin", "superadmin"));

        var logs = service.ReadLogsPage(1, 20, new Dictionary<string, object?>());

        Assert.True(logs.Succeeded);
        var log = Assert.Single(logs.Payload!.Events);
        Assert.Equal(101, log.ApiKeyId);
        Assert.Equal("Primary Key", log.ApiKeyName);

        var options = service.ReadLogFilterOption(
            "api_key_id",
            "Primary",
            new Dictionary<string, object?>());

        Assert.True(options.Succeeded);
        var apiKeyOptions = Assert.IsType<List<LogApiKeyFilterOption>>(options.Payload!["api_key_ids"]);
        var option = Assert.Single(apiKeyOptions);
        Assert.Equal(101, option.Id);
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

        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            context.Users.Add(new User
            {
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
                    Id = 601,
                    RequestId = "req-a",
                    CreatedAt = 1_700_000_000,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-4.1",
                    UpstreamModel = "gpt-4.1",
                    ChannelId = "alpha",
                    IsStream = true,
                    StatusCode = 200,
                    OwnerUsername = "admin",
                    InputTokens = 120,
                    CachedTokens = 30,
                    OutputTokens = 50,
                    Cost = 3.5
                },
                new RequestLog
                {
                    Id = 602,
                    RequestId = "req-b",
                    CreatedAt = 1_700_000_030,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-4.1",
                    UpstreamModel = "gpt-4.1",
                    ChannelId = "beta",
                    IsStream = false,
                    StatusCode = 500,
                    Error = "upstream failed",
                    OwnerUsername = "admin",
                    InputTokens = 80,
                    CachedTokens = 0,
                    OutputTokens = 20,
                    Cost = 1.2
                });
            context.SaveChanges();
        }

        var service = new ObservabilityService(
            new TestSettingsProvider(dbPath),
            new TestWorkContext("admin", "superadmin"));

        var stats = service.ReadStats(
            "custom",
            1_699_999_900,
            1_700_000_120,
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-4.1",
                ["channel_id"] = "alpha",
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
    public void LogsAndDetailsExposeLifecycleStatusesAndStreamLines()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            context.Users.Add(new User
            {
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
                    Id = 701,
                    RequestId = "req-queued",
                    CreatedAt = 1_700_000_000,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Queued,
                    IsStream = true,
                    OwnerUsername = "admin"
                },
                new RequestLog
                {
                    Id = 702,
                    RequestId = "req-processing",
                    CreatedAt = 1_700_000_010,
                    ProcessingStartedAt = 1_700_000_011,
                    Method = "POST",
                    Path = "/v1/responses",
                    Model = "gpt-test",
                    LifecycleStatus = ProxyRequestLifecycleStatus.Processing,
                    IsStream = true,
                    OwnerUsername = "admin"
                },
                new RequestLog
                {
                    Id = 703,
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
                    OwnerUsername = "admin",
                    Detail = new RequestLogDetail
                    {
                        RequestBody = "{\"model\":\"gpt-test\"}"
                    },
                    StreamLines =
                    [
                        new RequestLogStreamLine
                        {
                            Id = 1,
                            Sequence = 0,
                            OccurredAt = 1_700_000_020.100,
                            Source = "upstream",
                            RawLine = "event: response.output_text.delta"
                        },
                        new RequestLogStreamLine
                        {
                            Id = 2,
                            Sequence = 1,
                            OccurredAt = 1_700_000_020.120,
                            Source = "upstream",
                            RawLine = "data: {\"delta\":\"hi\"}"
                        }
                    ]
                },
                new RequestLog
                {
                    Id = 704,
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
                    OwnerUsername = "admin"
                });
            context.SaveChanges();
        }

        var service = new ObservabilityService(
            new TestSettingsProvider(dbPath),
            new TestWorkContext("admin", "superadmin"));

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

        var detail = service.ReadLogById(703);
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
                dbPath,
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

        public TestWorkContext(string username, string role)
        {
            _user = new SessionUser(username, role, true);
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
