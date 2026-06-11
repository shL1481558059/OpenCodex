using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyLogServiceTests
{
    [Fact]
    public void WriteLog_PersistsStreamTimingsJson()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-proxy-log-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var bootstrap = OpenCodexDbContextFactory.Create(dbPath))
        {
            bootstrap.Database.EnsureCreated();
            OpenCodexRequestLogs.EnsureSchema(bootstrap);
        }

        var settingsProvider = new TestSettingsProvider(dbPath);
        var pricing = new ModelPricingService(settingsProvider);
        var service = new ProxyLogService(settingsProvider, pricing);

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
            channelId: "lucky",
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

        using var context = OpenCodexDbContextFactory.Create(dbPath);
        var detail = context.RequestLogDetails.Single();
        Assert.NotNull(detail.StreamTimingsJson);
        Assert.Contains("\"first_output_text_delta_ms\":120", detail.StreamTimingsJson!, StringComparison.Ordinal);
        Assert.Contains("\"first_sse_event_ms\":15", detail.StreamTimingsJson!, StringComparison.Ordinal);
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
}
