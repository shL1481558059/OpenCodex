using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyLogServiceTests
{
    [Fact]
    public void WriteLogBuildsRequestLogRecordWithUsageAndCost()
    {
        var repository = new FakeProxyLogRepository
        {
            Usage = new UsageRecord(12, 3, 4),
            Cost = 0.25,
            LogId = 99
        };
        var service = new ProxyLogService(repository);

        var id = service.WriteLog(Context(
            upstreamResponse: new Dictionary<string, object?>
            {
                ["model"] = "upstream-model",
                ["usage"] = new Dictionary<string, object?>()
            },
            responsePayload: new Dictionary<string, object?> { ["output"] = "ok" },
            webSearchDetails: new Dictionary<string, object?> { ["calls"] = 1 }));

        Assert.Equal(99, id);
        Assert.Equal([( "chat", "upstream-model" )], repository.ExtractUsageCalls);
        Assert.Equal([( "upstream-model", 12, 3, 4 )], repository.CalculateCostCalls);

        var record = Assert.Single(repository.Records);
        Assert.Equal("req_123", record.RequestId);
        Assert.Equal("POST", record.Method);
        Assert.Equal("/v1/responses", record.Path);
        Assert.Equal("127.0.0.1", record.ClientIp);
        Assert.Equal("""{"Authorization":"Bearer abc...xyz","X-Test":"yes"}""", record.RequestHeaders);
        Assert.Equal("""{"model":"client-model"}""", record.RequestBody);
        Assert.Equal("""{"model":"upstream-model"}""", record.UpstreamRequestBody);
        Assert.Equal("""{"model":"upstream-model","usage":{}}""", record.UpstreamResponseBody);
        Assert.Equal("""{"output":"ok"}""", record.ResponseBody);
        Assert.Equal("""{"calls":1}""", record.WebSearchJson);
        Assert.Equal("client-model", record.Model);
        Assert.Equal("upstream-model", record.UpstreamModel);
        Assert.Equal("chat-channel", record.ChannelId);
        Assert.False(record.IsStream);
        Assert.Null(record.TtftMs);
        Assert.Equal(123, record.DurationMs);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal(12, record.InputTokens);
        Assert.Equal(3, record.CachedTokens);
        Assert.Equal(4, record.OutputTokens);
        Assert.Equal(0.25, record.Cost);
        Assert.Equal("alice", record.OwnerUsername);
        Assert.Equal(7L, record.ApiKeyId);
        Assert.Null(record.Error);
        Assert.True(record.CreatedAt > 0);
    }

    [Fact]
    public void WriteLogUsesErrorResponseAndZeroUsageWhenChannelTypeIsMissing()
    {
        var repository = new FakeProxyLogRepository
        {
            Cost = 0.0
        };
        var service = new ProxyLogService(repository);

        service.WriteLog(Context(
            upstreamResponse: null,
            responsePayload: null,
            errorResponse: new Dictionary<string, object?>
            {
                ["error"] = new Dictionary<string, object?> { ["message"] = "bad" }
            },
            channelType: null,
            isStream: true,
            ttftMs: 45,
            statusCode: 400,
            error: "bad request"));

        Assert.Empty(repository.ExtractUsageCalls);
        Assert.Equal([( "upstream-model", 0, 0, 0 )], repository.CalculateCostCalls);

        var record = Assert.Single(repository.Records);
        Assert.Equal("""{"error":{"message":"bad"}}""", record.ResponseBody);
        Assert.Null(record.WebSearchJson);
        Assert.True(record.IsStream);
        Assert.Equal(45, record.TtftMs);
        Assert.Equal(400, record.StatusCode);
        Assert.Equal("bad request", record.Error);
        Assert.Equal(0, record.InputTokens);
        Assert.Equal(0, record.CachedTokens);
        Assert.Equal(0, record.OutputTokens);
    }

    [Fact]
    public void WriteLogUsesRequestMetadata()
    {
        var repository = new FakeProxyLogRepository();
        var service = new ProxyLogService(repository);

        service.WriteLog(
            CoreContext(responsePayload: new Dictionary<string, object?> { ["output"] = "ok" }),
            new ProxyRequestMetadata(
                "POST",
                "/v1/responses",
                "203.0.113.10",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer abc...xyz",
                    ["X-Test"] = "yes"
                }));

        var record = Assert.Single(repository.Records);
        Assert.Equal("POST", record.Method);
        Assert.Equal("/v1/responses", record.Path);
        Assert.Equal("203.0.113.10", record.ClientIp);

        Assert.Equal("""{"Authorization":"Bearer abc...xyz","X-Test":"yes"}""", record.RequestHeaders);
    }

    private static ProxyLogContext CoreContext(
        Dictionary<string, object?>? responsePayload)
    {
        return new ProxyLogContext(
            "req_123",
            "alice",
            7,
            new Dictionary<string, object?> { ["model"] = "client-model" },
            new Dictionary<string, object?> { ["model"] = "upstream-model" },
            null,
            responsePayload,
            null,
            "client-model",
            "upstream-model",
            "chat-channel",
            "chat",
            IsStream: false,
            TtftMs: null,
            StatusCode: 200,
            DurationMs: 123,
            Error: null,
            WebSearchDetails: null);
    }

    private static ProxyRequestLogContext Context(
        Dictionary<string, object?>? upstreamResponse,
        Dictionary<string, object?>? responsePayload,
        object? errorResponse = null,
        Dictionary<string, object?>? webSearchDetails = null,
        string? channelType = "chat",
        bool isStream = false,
        int? ttftMs = null,
        int statusCode = 200,
        string? error = null)
    {
        return new ProxyRequestLogContext(
            "req_123",
            "alice",
            7,
            new Dictionary<string, object?> { ["model"] = "client-model" },
            new Dictionary<string, object?> { ["model"] = "upstream-model" },
            upstreamResponse,
            responsePayload,
            errorResponse,
            "client-model",
            "upstream-model",
            "chat-channel",
            channelType,
            isStream,
            ttftMs,
            statusCode,
            123,
            error,
            webSearchDetails,
            "POST",
            "/v1/responses",
            "127.0.0.1",
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer abc...xyz",
                ["X-Test"] = "yes"
            });
    }

    private sealed class FakeProxyLogRepository : IProxyLogRepository
    {
        public UsageRecord Usage { get; init; } = new(0, 0, 0);

        public double Cost { get; init; }

        public long LogId { get; init; } = 1;

        public List<(string Protocol, string Model)> ExtractUsageCalls { get; } = [];

        public List<(string Model, int InputTokens, int CachedTokens, int OutputTokens)> CalculateCostCalls { get; } = [];

        public List<RequestLogWriteRecord> Records { get; } = [];

        public UsageRecord ExtractUsage(
            IReadOnlyDictionary<string, object?> response,
            string protocol)
        {
            ExtractUsageCalls.Add((protocol, response.TryGetValue("model", out var model) ? model?.ToString() ?? string.Empty : string.Empty));
            return Usage;
        }

        public double CalculateCost(
            string model,
            int inputTokens,
            int cachedTokens,
            int outputTokens)
        {
            CalculateCostCalls.Add((model, inputTokens, cachedTokens, outputTokens));
            return Cost;
        }

        public long WriteRequestLog(RequestLogWriteRecord record)
        {
            Records.Add(record);
            return LogId;
        }
    }
}
