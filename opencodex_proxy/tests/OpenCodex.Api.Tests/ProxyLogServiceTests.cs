using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyLogServiceTests
{
    private static readonly Guid TestApiKeyId = Guid.Parse("55555555-5555-5555-5555-555555555501");
    private static readonly Guid TestChannelId = Guid.Parse("66666666-6666-6666-6666-666666666601");

    [Fact]
    public async Task WriteLog_PreservesNestedMcpAuthorizationTokens()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-proxy-log-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var bootstrap = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            bootstrap.Database.Migrate();
        }

        EnsureAdminUser(dbPath);
        var service = CreateService(dbPath);
        await service.WriteLogAsync(new ProxyRequestLogContext(
            requestId: "req-mcp-redaction",
            ownerUsername: "admin",
            apiKeyId: null,
            payload: new Dictionary<string, object?>
            {
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "mcp", ["authorization"] = "openai-secret" }
                },
                ["mcp_servers"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["authorization_token"] = "anthropic-secret" }
                }
            },
            upstreamRequest: new Dictionary<string, object?>
            {
                ["headers"] = new Dictionary<string, object?> { ["Authorization"] = "Bearer nested-secret" }
            },
            upstreamResponse: new Dictionary<string, object?>(),
            responsePayload: new Dictionary<string, object?>(),
            errorResponse: null,
            requestModel: "gpt",
            upstreamModel: "claude",
            channelId: TestChannelId.ToString(),
            channelType: "messages",
            isStream: false,
            ttftMs: null,
            statusCode: 200,
            durationMs: 1,
            error: null,
            webSearchDetails: null,
            method: "POST",
            path: "/v1/messages",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string> { ["authorization"] = "Bearer client-secret" }));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        var logId = context.RequestLogs.Single().Id;
        var content = new LogContentStore(context).Read(logId);
        var combined = $"{content.Get(RequestLogContentSlot.RequestHeaders)}\n"
            + $"{content.Get(RequestLogContentSlot.RequestBody)}\n"
            + content.Get(RequestLogContentSlot.UpstreamRequestBody);
        Assert.Contains("openai-secret", combined, StringComparison.Ordinal);
        Assert.Contains("anthropic-secret", combined, StringComparison.Ordinal);
        Assert.Contains("nested-secret", combined, StringComparison.Ordinal);
        Assert.Contains("client-secret", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLog_PreservesNestedImageDataInObjectsAndArrays()
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["image_url"] = "data:image/png;base64,request-secret"
                        }
                    }
                }
            },
            ["nested"] = new Dictionary<string, object?> { ["b64_json"] = "request-b64-secret" }
        };

        var detail = await WriteImageLogAsync(payload: payload);

        var requestBody = detail.Get(RequestLogContentSlot.RequestBody);
        Assert.Contains("request-secret", requestBody, StringComparison.Ordinal);
        Assert.Contains("request-b64-secret", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLog_PreservesImageDataInUpstreamErrorResponse()
    {
        var errorResponse = new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["preview"] = "data:image/jpeg;base64,error-secret",
                ["details"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["b64_json"] = "error-b64-secret" }
                }
            }
        };

        var detail = await WriteImageLogAsync(errorResponse: errorResponse, statusCode: 502, error: "upstream failed");

        var responseBody = detail.Get(RequestLogContentSlot.ResponseBody);
        Assert.Contains("error-secret", responseBody, StringComparison.Ordinal);
        Assert.Contains("error-b64-secret", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLog_DoesNotModifyClientResponseWhileSanitizingStoredLog()
    {
        var responsePayload = new Dictionary<string, object?>
        {
            ["data"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["b64_json"] = "client-b64-secret",
                    ["url"] = "data:image/webp;base64,client-data-secret"
                }
            }
        };

        var detail = await WriteImageLogAsync(responsePayload: responsePayload);

        var originalItem = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<List<object?>>(responsePayload["data"])));
        Assert.Equal("client-b64-secret", originalItem["b64_json"]);
        Assert.Equal("data:image/webp;base64,client-data-secret", originalItem["url"]);
        var storedResponse = detail.Get(RequestLogContentSlot.ResponseBody);
        Assert.Contains("client-b64-secret", storedResponse, StringComparison.Ordinal);
        Assert.Contains("client-data-secret", storedResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLog_PreservesLongBase64SentinelWithoutMutatingSource()
    {
        var sentinel = new string('Z', 128 * 1024);
        var dataUri = $"data:image/png;base64,{sentinel}";
        var payload = new Dictionary<string, object?> { ["image_url"] = dataUri };

        var detail = await WriteImageLogAsync(payload: payload);

        Assert.Equal(dataUri, payload["image_url"]);
        Assert.Contains(sentinel, detail.Get(RequestLogContentSlot.RequestBody), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLog_PreservesBinaryByteValues()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var payload = new Dictionary<string, object?> { ["bytes"] = bytes };

        var detail = await WriteImageLogAsync(payload: payload);

        Assert.Contains("AQIDBA==", detail.Get(RequestLogContentSlot.RequestBody), StringComparison.Ordinal);
        Assert.Same(bytes, payload["bytes"]);
    }

    [Fact]
    public async Task WriteLog_DoesNotPersistDetailedStreamTimings()
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

        await service.WriteLogAsync(new ProxyRequestLogContext(
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
            requestHeaders: new Dictionary<string, string>()));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        var log = context.RequestLogs.Single();
        var references = context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == log.Id)
            .ToList();
        Assert.Equal(120, log.TtftMs);
        Assert.Equal(5, references.Count);
        Assert.DoesNotContain(
            context.LogContentBlocks.AsNoTracking(),
            block => block.Compression == LogContentCodec.RawCodec
                && System.Text.Encoding.UTF8.GetString(block.Data).Contains(
                    "first_output_text_delta_ms",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteLog_PreservesRawRequestBodyAndIndexesConversationMetadata()
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
        const string rawBody = "{\n  \"model\": \"gpt-5\",\n  \"value\": 1.00,\n  \"previous_response_id\": \"resp-parent\"\n}";
        var headers = new Dictionary<string, string>
        {
            ["x-codex-turn-metadata"] = "{\"session_id\":\"session-1\",\"thread_id\":\"thread-1\","
                + "\"turn_id\":\"turn-7\",\"window_id\":\"window-2\"}"
        };
        var service = CreateService(dbPath);
        await service.WriteLogAsync(
            new ProxyLogContext(
                RequestId: "req-raw-body",
                OwnerUsername: "admin",
                ApiKeyId: null,
                Payload: new Dictionary<string, object?>
                {
                    ["model"] = "gpt-5",
                    ["value"] = 1d,
                    ["previous_response_id"] = "resp-parent"
                },
                UpstreamRequest: new Dictionary<string, object?>(),
                UpstreamResponse: new Dictionary<string, object?>(),
                ResponsePayload: new Dictionary<string, object?>(),
                ErrorResponse: null,
                RequestModel: "gpt-5",
                UpstreamModel: "gpt-5",
                ChannelId: TestChannelId.ToString(),
                ChannelType: "responses",
                IsStream: false,
                TtftMs: null,
                StatusCode: 200,
                DurationMs: 1,
                Error: null,
                WebSearchDetails: null),
            new ProxyRequestMetadata(
                "POST",
                "/v1/responses",
                "127.0.0.1",
                headers,
                rawBody));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var log = context.RequestLogs.Single();
        var content = new LogContentStore(context).Read(log.Id);
        Assert.Equal(rawBody, content.Get(RequestLogContentSlot.RequestBody));
        Assert.Equal("thread:thread-1", log.ConversationKey);
        Assert.Equal("turn-7", log.ConversationTurnId);
        Assert.Equal("window-2", log.ConversationWindowId);
        Assert.Equal("resp-parent", log.PreviousResponseId);
    }

    [Fact]
    public async Task WriteLog_IndexesClaudeCodeSessionHeaderAsConversationKey()
    {
        // Claude Code CLI 直连 /v1/messages 时只携带 X-Claude-Code-Session-Id,
        // 不带 x-codex-turn-metadata / thread-id / session-id 头,请求体也没有 prompt_cache_key。
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
        var headers = new Dictionary<string, string>
        {
            ["X-Claude-Code-Session-Id"] = "claude-session-42"
        };
        var service = CreateService(dbPath);
        await service.WriteLogAsync(
            new ProxyLogContext(
                RequestId: "req-claude",
                OwnerUsername: "admin",
                ApiKeyId: null,
                Payload: new Dictionary<string, object?>
                {
                    ["model"] = "claude-sonnet-4",
                    ["messages"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "hello"
                        }
                    }
                },
                UpstreamRequest: new Dictionary<string, object?>(),
                UpstreamResponse: new Dictionary<string, object?>(),
                ResponsePayload: new Dictionary<string, object?>(),
                ErrorResponse: null,
                RequestModel: "claude-sonnet-4",
                UpstreamModel: "claude-sonnet-4",
                ChannelId: TestChannelId.ToString(),
                ChannelType: "messages",
                IsStream: false,
                TtftMs: null,
                StatusCode: 200,
                DurationMs: 1,
                Error: null,
                WebSearchDetails: null),
            new ProxyRequestMetadata(
                "POST",
                "/v1/messages",
                "127.0.0.1",
                headers,
                null));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var log = context.RequestLogs.Single();
        Assert.Equal("session:claude-session-42", log.ConversationKey);
    }

    [Fact]
    public async Task LifecycleMethods_PersistStatusesAndStreamLines()
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
            var queuedContent = new LogContentStore(context).Read(requestLogId);
            Assert.Equal(ProxyRequestLifecycleStatus.Queued, queued.LifecycleStatus);
            Assert.Null(queued.ProcessingStartedAt);
            Assert.Null(queued.CompletedAt);
            Assert.Contains(
                "\"model\":\"gpt-5\"",
                queuedContent.Get(RequestLogContentSlot.RequestBody),
                StringComparison.Ordinal);
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
            var processingContent = new LogContentStore(context).Read(requestLogId);
            Assert.Equal(ProxyRequestLifecycleStatus.Processing, processing.LifecycleStatus);
            Assert.NotNull(processing.ProcessingStartedAt);
            Assert.Equal(TestApiKeyId, processing.ApiKeyId);
            Assert.Equal(TestChannelId, processing.ChannelId);
            Assert.Contains(
                "\"stream\":true",
                processingContent.Get(RequestLogContentSlot.UpstreamRequestBody),
                StringComparison.Ordinal);
        }

        await service.CompleteLogAsync(
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
                    new ProxyRequestStreamLineCapture(0, "upstream", "event: response.output_text.delta"),
                    new ProxyRequestStreamLineCapture(1, "upstream", "data: {\"delta\":\"hello\"}"),
                    new ProxyRequestStreamLineCapture(2, "upstream", string.Empty)
                ]),
            new ProxyRequestMetadata("POST", "/v1/responses", "127.0.0.1", new Dictionary<string, string>()));

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            var completed = context.RequestLogs
                .Single(item => item.Id == requestLogId);
            var completedContent = new LogContentStore(context).Read(requestLogId);
            using var streamDocument = JsonDocument.Parse(
                completedContent.Get(RequestLogContentSlot.StreamLinesJson)!);
            var completedStreamLines = streamDocument.RootElement.EnumerateArray().ToList();
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
                    Assert.False(line.TryGetProperty("occurred_at", out _));
                    Assert.Equal(0, line.GetProperty("sequence").GetInt32());
                    Assert.Equal("upstream", line.GetProperty("source").GetString());
                    Assert.Equal("event: response.output_text.delta", line.GetProperty("raw_line").GetString());
                },
                line =>
                {
                    Assert.Equal(1, line.GetProperty("sequence").GetInt32());
                    Assert.Equal("data: {\"delta\":\"hello\"}", line.GetProperty("raw_line").GetString());
                },
                line =>
                {
                    Assert.Equal(2, line.GetProperty("sequence").GetInt32());
                    Assert.Equal(string.Empty, line.GetProperty("raw_line").GetString());
                });
        }
    }

    private static ProxyLogService CreateService(string dbPath)
    {
        var settingsProvider = new TestSettingsProvider(dbPath);
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var catalog = new ModelCatalogService(
            new EfRepository<ModelProvider>(context),
            new EfRepository<ModelInfo>(context),
            new EfRepository<ChannelModelInfo>(context),
            new EfRepository<ModelPricingPlan>(context),
            new EfRepository<ModelPricingRule>(context),
            new EfRepository<ChannelModelMapping>(context),
            new EfRepository<Channel>(context),
            new TestWorkContext(Guid.Parse("55555555-5555-5555-5555-555555555599"), "admin", "superadmin"),
            new TestCacheService());
        return new ProxyLogService(
            settingsProvider,
            catalog,
            context,
            new EfRepository<RequestLog>(context),
            new EfRepository<User>(context));
    }

    private static async Task<LogContentSnapshot> WriteImageLogAsync(
        Dictionary<string, object?>? payload = null,
        Dictionary<string, object?>? responsePayload = null,
        object? errorResponse = null,
        int statusCode = 200,
        string? error = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-proxy-log-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var bootstrap = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            bootstrap.Database.Migrate();
        }

        EnsureAdminUser(dbPath);
        var service = CreateService(dbPath);
        await service.WriteLogAsync(new ProxyRequestLogContext(
            requestId: $"req-image-log-{Guid.NewGuid():N}",
            ownerUsername: "admin",
            apiKeyId: null,
            payload: payload ?? new Dictionary<string, object?>(),
            upstreamRequest: new Dictionary<string, object?>(),
            upstreamResponse: new Dictionary<string, object?>(),
            responsePayload: responsePayload,
            errorResponse: errorResponse,
            requestModel: "gpt-image-1",
            upstreamModel: "gpt-image-1",
            channelId: TestChannelId.ToString(),
            channelType: "images",
            isStream: false,
            ttftMs: null,
            statusCode: statusCode,
            durationMs: 1,
            error: error,
            webSearchDetails: null,
            method: "POST",
            path: "/v1/images/generations",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string>()));

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        var logId = context.RequestLogs.AsNoTracking().Single().Id;
        return new LogContentStore(context).Read(logId);
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
