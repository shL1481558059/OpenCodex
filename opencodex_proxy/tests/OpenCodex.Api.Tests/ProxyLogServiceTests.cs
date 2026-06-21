using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyLogServiceTests
{
    private static readonly Guid TestApiKeyId = Guid.Parse("55555555-5555-5555-5555-555555555501");
    private static readonly Guid TestChannelId = Guid.Parse("66666666-6666-6666-6666-666666666601");

    [Fact]
    public void WriteLog_PersistsStreamTimingsJson()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-proxy-log-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var bootstrap = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            bootstrap.Database.Migrate();
        }

        EnsureAdminUser(dbPath);
        var service = CreateService(dbPath);

        service.WriteLog(new ProxyRequestLogContext(
            requestId: "req-stream-1",
            ownerUsername: "admin",
            apiKeyId: null,
            payload: new Dictionary<string, object?> { ["model"] = "gpt-5" },
            upstreamRequest: new Dictionary<string, object?> { ["stream"] = true },
            upstreamResponse: new Dictionary<string, object?>
            {
                ["model"] = "gpt-5",
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = 1,
                    ["output_tokens"] = 2
                }
            },
            responsePayload: new Dictionary<string, object?> { ["id"] = "resp-1" },
            errorResponse: null,
            requestModel: "gpt-5",
            upstreamModel: "gpt-5",
            channelId: TestChannelId.ToString(),
            channelType: "responses",
            isStream: true,
            ttftMs: 120,
            statusCode: 200,
            durationMs: 350,
            error: null,
            webSearchDetails: null,
            method: "POST",
            path: "/v1/responses",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string>(),
            streamWriteMetrics: new StreamWriteMetrics(
                ttftMs: 120,
                firstSseEventMs: 15,
                firstReasoningSummaryTextDeltaMs: 70,
                firstOutputTextDeltaMs: 120,
                firstFunctionCallArgumentsDeltaMs: null,
                completedEventMs: 340)));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
var detail = context.RequestLogDetails.Single();
        Assert.NotNull(detail.StreamTimingsJson);
        Assert.Contains("\"first_output_text_delta_ms\":120", detail.StreamTimingsJson!, StringComparison.Ordinal);
        Assert.Contains("\"first_sse_event_ms\":15", detail.StreamTimingsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleMethods_PersistStatusesAndStreamLines()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-proxy-log-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var bootstrap = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            bootstrap.Database.Migrate();
        }

        EnsureAdminUser(dbPath);
        var service = CreateService(dbPath);

        var requestLogId = service.CreateQueuedLog(new ProxyRequestLogQueuedContext(
            requestId: "req-lifecycle-1",
            ownerUsername: "admin",
            apiKeyId: null,
            payload: new Dictionary<string, object?> { ["model"] = "gpt-5" },
            requestModel: "gpt-5",
            isStream: true,
            method: "POST",
            path: "/v1/responses",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string>()));

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
var queued = context.RequestLogs
                .Single(item => item.Id == requestLogId);
            var queuedDetail = context.RequestLogDetails.Single(d => d.RequestLogId == requestLogId);
            Assert.Equal(ProxyRequestLifecycleStatus.Queued, queued.LifecycleStatus);
            Assert.Null(queued.ProcessingStartedAt);
            Assert.Null(queued.CompletedAt);
            Assert.NotNull(queuedDetail);
            Assert.Contains("\"model\":\"gpt-5\"", queuedDetail.RequestBody!, StringComparison.Ordinal);
        }

        service.MarkProcessing(requestLogId, new ProxyRequestLogProcessingContext(
            ownerUsername: "admin",
            apiKeyId: TestApiKeyId,
            upstreamRequest: new Dictionary<string, object?> { ["stream"] = true, ["model"] = "upstream-gpt-5" },
            requestModel: "gpt-5",
            upstreamModel: "upstream-gpt-5",
            channelId: TestChannelId.ToString(),
            channelType: "responses",
            isStream: true));

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
var processing = context.RequestLogs
                .Single(item => item.Id == requestLogId);
            var processingDetail = context.RequestLogDetails.Single(d => d.RequestLogId == requestLogId);
            Assert.Equal(ProxyRequestLifecycleStatus.Processing, processing.LifecycleStatus);
            Assert.NotNull(processing.ProcessingStartedAt);
            Assert.Equal(TestApiKeyId, processing.ApiKeyId);
            Assert.Equal(TestChannelId, processing.ChannelId);
            Assert.Contains("\"stream\":true", processingDetail.UpstreamRequestBody!, StringComparison.Ordinal);
        }

        service.CompleteLog(
            requestLogId,
            new ProxyLogContext(
                RequestId: "req-lifecycle-1",
                OwnerUsername: "admin",
                ApiKeyId: TestApiKeyId,
                Payload: new Dictionary<string, object?> { ["model"] = "gpt-5" },
                UpstreamRequest: new Dictionary<string, object?> { ["stream"] = true, ["model"] = "upstream-gpt-5" },
                UpstreamResponse: new Dictionary<string, object?>
                {
                    ["model"] = "upstream-gpt-5",
                    ["usage"] = new Dictionary<string, object?>
                    {
                        ["input_tokens"] = 2,
                        ["output_tokens"] = 3
                    }
                },
                ResponsePayload: new Dictionary<string, object?> { ["id"] = "resp-1" },
                ErrorResponse: null,
                RequestModel: "gpt-5",
                UpstreamModel: "upstream-gpt-5",
                ChannelId: TestChannelId.ToString(),
                ChannelType: "responses",
                IsStream: true,
                TtftMs: 88,
                StatusCode: 200,
                DurationMs: 320,
                Error: null,
                WebSearchDetails: null,
                StreamLines:
                [
                    new ProxyRequestStreamLineCapture(0, 1_700_000_000.100, "upstream", "event: response.output_text.delta"),
                    new ProxyRequestStreamLineCapture(1, 1_700_000_000.120, "upstream", "data: {\"delta\":\"hello\"}"),
                    new ProxyRequestStreamLineCapture(2, 1_700_000_000.121, "upstream", string.Empty)
                ]),
            new ProxyRequestMetadata("POST", "/v1/responses", "127.0.0.1", new Dictionary<string, string>()));

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
var completed = context.RequestLogs
                .Single(item => item.Id == requestLogId);
            var completedStreamLines = context.RequestLogStreamLines
                .Where(line => line.RequestLogId == requestLogId)
                .OrderBy(line => line.Sequence)
                .ToList();
            Assert.Equal(ProxyRequestLifecycleStatus.Success, completed.LifecycleStatus);
            Assert.NotNull(completed.CompletedAt);
            Assert.Equal(88, completed.TtftMs);
            Assert.Equal(320, completed.DurationMs);
            Assert.Equal(2, completed.InputTokens);
            Assert.Equal(3, completed.OutputTokens);
            Assert.Equal(3, completedStreamLines.Count);
            Assert.Collection(
                completedStreamLines,
                line =>
                {
                    Assert.Equal(0, line.Sequence);
                    Assert.Equal("upstream", line.Source);
                    Assert.Equal("event: response.output_text.delta", line.RawLine);
                },
                line =>
                {
                    Assert.Equal(1, line.Sequence);
                    Assert.Equal("data: {\"delta\":\"hello\"}", line.RawLine);
                },
                line =>
                {
                    Assert.Equal(2, line.Sequence);
                    Assert.Equal(string.Empty, line.RawLine);
                });
        }
    }

    private static ProxyLogService CreateService(string dbPath)
    {
        var settingsProvider = new TestSettingsProvider(dbPath);
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var pricing = new ModelPricingService(new EfRepository<ModelPricing>(context));
        return new ProxyLogService(
            settingsProvider,
            pricing,
            new EfRepository<RequestLog>(context),
            new EfRepository<RequestLogDetail>(context),
            new EfRepository<RequestLogStreamLine>(context),
            new EfRepository<User>(context));
    }

    private static void EnsureAdminUser(string dbPath)
    {
        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        if (!context.Users.Any(u => u.Username == "admin"))
        {
            context.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = "hash",
                Role = "superadmin",
                Enabled = true,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            context.SaveChanges();
        }
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
}
