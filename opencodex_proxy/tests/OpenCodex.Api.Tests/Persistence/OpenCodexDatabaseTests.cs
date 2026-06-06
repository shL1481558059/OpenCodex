using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using System.Text.Json;

namespace OpenCodex.Api.Tests.Persistence;

public sealed class OpenCodexDatabaseTests
{
    [Fact]
    public void InitializeCreatesSchemaAndIsIdempotent()
    {
        using var workspace = new TempWorkspace();
        var dbPath = Path.Combine(workspace.Path, "nested", "opencodex.db");

        OpenCodexDatabase.Initialize(dbPath);
        OpenCodexDatabase.Initialize(dbPath);

        using var connection = OpenConnection(dbPath);
        var tables = ReadNames(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        var indexes = ReadNames(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name");

        Assert.Contains("request_logs", tables);
        Assert.Contains("request_log_details", tables);
        Assert.Contains("channels", tables);
        Assert.Contains("users", tables);
        Assert.Contains("access_api_keys", tables);
        Assert.Contains("web_search_settings", tables);
        Assert.Contains("tavily_keys", tables);
        Assert.Contains("idx_channels_owner_position", indexes);
        Assert.Contains("idx_request_logs_owner_id", indexes);
    }

    [Fact]
    public void ReplaceAndReadChannelsPreservesOrderAndJsonFields()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [
                Obj(
                    ("id", "first"),
                    ("name", "First"),
                    ("type", "chat"),
                    ("baseurl", "https://first.example.test/v1"),
                    ("apikey", "${FIRST_KEY}"),
                    ("auth_mode", "config"),
                    ("headers", Obj(("X-Test", "yes"))),
                    ("timeout_seconds", 45),
                    ("retry_count", 2),
                    ("compat", Obj(("drop_params", List("store")))),
                    ("models", List(Obj(("model", "gpt-5"), ("upstream_model", "gpt-4")))),
                    ("enabled", false)),
                Obj(
                    ("id", "second"),
                    ("type", "messages"),
                    ("baseurl", "https://second.example.test/v1"))
            ],
            defaultTimeout: 30);

        var channels = OpenCodexDatabase.ReadChannels(dbPath);

        Assert.Equal(["first", "second"], channels.Select(channel => channel.Id));
        Assert.Equal("yes", channels[0].Headers["X-Test"]);
        Assert.Equal(2, channels[0].RetryCount);
        Assert.Equal(["store"], AsList(channels[0].Compat["drop_params"]));
        var model = Assert.Single(channels[0].Models);
        Assert.Equal("gpt-5", AsObject(model)["model"]);
        Assert.Equal("gpt-4", AsObject(model)["upstream_model"]);
        Assert.False(channels[0].Enabled);
        Assert.Equal(30, channels[1].TimeoutSeconds);
        Assert.Equal(3, channels[1].RetryCount);
        Assert.Equal("config", channels[1].AuthMode);
        Assert.True(channels[1].Enabled);
    }

    [Fact]
    public void ReplaceChannelsRemovesDeletedRows()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [
                Obj(("id", "old"), ("type", "chat"), ("baseurl", "https://old.example.test/v1")),
                Obj(("id", "keep"), ("type", "chat"), ("baseurl", "https://keep.example.test/v1"))
            ]);
        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [
                Obj(("id", "keep"), ("type", "chat"), ("baseurl", "https://keep.example.test/v1"))
            ]);

        var channels = OpenCodexDatabase.ReadChannels(dbPath);

        Assert.Equal(["keep"], channels.Select(channel => channel.Id));
    }

    [Fact]
    public void ReplaceChannelsScopesRowsByOwner()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [Obj(("id", "chat"), ("type", "chat"), ("baseurl", "https://alice.example.test/v1"))],
            ownerUsername: "alice");
        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [Obj(("id", "chat"), ("type", "chat"), ("baseurl", "https://bob.example.test/v1"))],
            ownerUsername: "bob");

        var allChannels = OpenCodexDatabase.ReadChannels(dbPath);
        var aliceChannels = OpenCodexDatabase.ReadChannels(dbPath, ownerUsername: "alice");
        var bobChannels = OpenCodexDatabase.ReadChannels(dbPath, ownerUsername: "bob");

        Assert.Equal(
            [("alice", "chat"), ("bob", "chat")],
            allChannels.Select(channel => (channel.OwnerUsername, channel.Id)));
        Assert.Equal("https://alice.example.test/v1", aliceChannels[0].BaseUrl);
        Assert.Equal("https://bob.example.test/v1", bobChannels[0].BaseUrl);
    }

    [Fact]
    public void InitializeMigratesLegacyChannelDefaultsAndOwnerPrimaryKey()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        using (var connection = OpenConnection(dbPath))
        {
            ExecuteNonQuery(
                connection,
                """
                CREATE TABLE channels (
                    id TEXT PRIMARY KEY,
                    position INTEGER NOT NULL,
                    name TEXT NOT NULL DEFAULT '',
                    type TEXT NOT NULL,
                    baseurl TEXT NOT NULL,
                    apikey TEXT NOT NULL DEFAULT '',
                    auth_mode TEXT NOT NULL DEFAULT 'removed_auth_mode',
                    headers_json TEXT NOT NULL DEFAULT '{}',
                    timeout_seconds INTEGER NOT NULL,
                    compat_json TEXT NOT NULL DEFAULT '{}',
                    enabled INTEGER NOT NULL DEFAULT 1,
                    created_at REAL NOT NULL,
                    updated_at REAL NOT NULL
                );

                INSERT INTO channels (
                    id, position, name, type, baseurl, apikey, auth_mode,
                    headers_json, timeout_seconds, compat_json, enabled,
                    created_at, updated_at
                ) VALUES (
                    'legacy', 0, '', 'chat', 'https://legacy.example.test/v1', '',
                    'removed_auth_mode', '{}', 30, '{}', 1, 1.0, 1.0
                );
                """);
        }

        OpenCodexDatabase.Initialize(dbPath, defaultOwnerUsername: "root");

        using var migratedConnection = OpenConnection(dbPath);
        var columns = ReadNames(migratedConnection, "PRAGMA table_info(channels)", ordinal: 1);
        var primaryKey = ReadChannelPrimaryKey(migratedConnection);
        using var command = migratedConnection.CreateCommand();
        command.CommandText = "SELECT owner_username, auth_mode, retry_count FROM channels WHERE id = 'legacy'";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("root", reader.GetString(0));
        Assert.Equal("config", reader.GetString(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.Contains("models_json", columns);
        Assert.Contains("retry_count", columns);
        Assert.Equal(["owner_username", "id"], primaryKey);
    }

    [Fact]
    public void ExtractUsagePreservesProtocolSpecificTokenFields()
    {
        var responses = OpenCodexDatabase.ExtractUsage(
            Obj(
                ("usage", Obj(
                    ("input_tokens", 100),
                    ("input_tokens_details", Obj(("cached_tokens", 30))),
                    ("output_tokens", 50)))),
            "responses");
        var messages = OpenCodexDatabase.ExtractUsage(
            Obj(
                ("usage", Obj(
                    ("input_tokens", 100),
                    ("cache_creation_input_tokens", 10),
                    ("cache_read_input_tokens", 20),
                    ("output_tokens", 50)))),
            "messages");
        var chat = OpenCodexDatabase.ExtractUsage(
            Obj(
                ("usage", Obj(
                    ("prompt_tokens", 100),
                    ("prompt_tokens_details", Obj(("cached_tokens", 25))),
                    ("completion_tokens", 50)))),
            "chat");
        var missing = OpenCodexDatabase.ExtractUsage(Obj(), "responses");

        Assert.Equal(new UsageRecord(100, 30, 50), responses);
        Assert.Equal(new UsageRecord(100, 30, 50), messages);
        Assert.Equal(new UsageRecord(100, 25, 50), chat);
        Assert.Equal(new UsageRecord(0, 0, 0), missing);
    }

    [Fact]
    public void CalculateCostPreservesKnownModelPricingAndFuzzyMatch()
    {
        Assert.Equal(
            (50000 * 3 + 50000 * 0.025 + 2000 * 6) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("deepseek-v4-pro", 100000, 50000, 2000),
            precision: 12);
        Assert.Equal(
            (50000 * 1 + 50000 * 0.02 + 2000 * 2) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("deepseek-v4-flash", 100000, 50000, 2000),
            precision: 12);
        Assert.Equal(
            (10000 * 6 + 10000 * 1.3 + 2000 * 24) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("glm-5.1", 20000, 10000, 2000),
            precision: 12);
        Assert.Equal(
            (20000 * 8 + 30000 * 2 + 2000 * 28) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("glm-5.1", 50000, 30000, 2000),
            precision: 12);
        Assert.Equal(
            (500 * 18.25 + 500 * 1.82 + 500 * 109.5) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("gpt-5.4", 1000, 500, 500),
            precision: 12);
        Assert.Equal(
            (1000 * 5.47 + 0 * 0.55 + 500 * 32.85) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("gpt-5.4-mini", 1000, 0, 500),
            precision: 12);
        Assert.Equal(
            (500 * 36.5 + 500 * 3.65 + 500 * 219.0) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("gpt-5.5", 1000, 500, 500),
            precision: 12);
        Assert.Equal(
            (50000 * 3 + 50000 * 0.025 + 2000 * 6) / 1_000_000.0,
            OpenCodexDatabase.CalculateCost("deepseek-v4-pro-20260501", 100000, 50000, 2000),
            precision: 12);
        Assert.Equal(0.0, OpenCodexDatabase.CalculateCost("unknown-model", 1000, 0, 500));
    }

    [Fact]
    public void WriteAndReadRequestLogsKeepMetadataAndDetails()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var webSearchCall = Obj(("query", "OpenAI"));
        var upstreamRequestMessage = Obj(("role", "user"), ("content", "raw"));
        var upstreamChoice = Obj(("message", Obj(("content", "upstream"))));
        var responseItem = Obj(("content", List(Obj(("text", "final")))));
        var webSearchJson = JsonSerializer.Serialize(Obj(("calls", List(webSearchCall))));
        var requestBodyJson = JsonSerializer.Serialize(Obj(("input", "raw")));
        var upstreamRequestBodyJson = JsonSerializer.Serialize(
            Obj(("messages", List(upstreamRequestMessage))));
        var upstreamResponseBodyJson = JsonSerializer.Serialize(
            Obj(("choices", List(upstreamChoice))));
        var responseBodyJson = JsonSerializer.Serialize(
            Obj(("output", List(responseItem))));

        var logId = OpenCodexDatabase.WriteRequestLog(
            dbPath,
            Obj(
                ("request_id", "req_web"),
                ("created_at", 123.5),
                ("method", "POST"),
                ("path", "/v1/responses"),
                ("client_ip", "127.0.0.1"),
                ("request_headers", "{}"),
                ("request_body", requestBodyJson),
                ("upstream_request_body", upstreamRequestBodyJson),
                ("model", "gpt-4o"),
                ("upstream_model", "gpt-4o"),
                ("channel_id", "openai"),
                ("is_stream", 0),
                ("duration_ms", 100),
                ("status_code", 200),
                ("upstream_response_body", upstreamResponseBodyJson),
                ("response_body", responseBodyJson),
                ("input_tokens", 100),
                ("cached_tokens", 0),
                ("output_tokens", 50),
                ("cost", 0.001),
                ("owner_username", "alice"),
                ("api_key_id", 7),
                ("web_search_json", webSearchJson)));

        var logs = OpenCodexDatabase.ReadLogs(dbPath);
        var detail = OpenCodexDatabase.ReadLogById(dbPath, logId);

        var log = Assert.Single(logs);
        Assert.NotNull(detail);
        Assert.Equal(logId, log.Id);
        Assert.Equal("req_web", log.RequestId);
        Assert.Equal("gpt-4o", log.Model);
        Assert.Equal(100, log.InputTokens);
        Assert.Equal("alice", log.OwnerUsername);
        Assert.Equal(7, log.ApiKeyId);
        Assert.Equal("success", log.RequestStatus);
        Assert.Equal("OpenAI", JsonDocument.Parse(detail.WebSearchJson!).RootElement.GetProperty("calls")[0].GetProperty("query").GetString());
        Assert.Equal("raw", JsonDocument.Parse(detail.RequestBody!).RootElement.GetProperty("input").GetString());
        Assert.Equal("raw", JsonDocument.Parse(detail.UpstreamRequestBody!).RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("upstream", JsonDocument.Parse(detail.UpstreamResponseBody!).RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("final", JsonDocument.Parse(detail.ResponseBody!).RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadLogsOrdersByNewestAndAppliesLimit()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        for (var index = 0; index < 5; index++)
        {
            OpenCodexDatabase.WriteRequestLog(
                dbPath,
                Obj(
                    ("request_id", $"req_{index}"),
                    ("created_at", 100 + index),
                    ("method", "POST"),
                    ("path", "/v1/responses"),
                    ("client_ip", "127.0.0.1"),
                    ("model", "gpt-4o"),
                    ("upstream_model", "gpt-4o"),
                    ("channel_id", "openai"),
                    ("status_code", index == 4 ? 500 : 200),
                    ("error", index == 4 ? "boom" : null)));
        }

        var logs = OpenCodexDatabase.ReadLogs(dbPath, limit: 3);

        Assert.Equal(["req_4", "req_3", "req_2"], logs.Select(item => item.RequestId));
        Assert.Equal("failed", logs[0].RequestStatus);
        Assert.Equal("success", logs[1].RequestStatus);
    }

    [Fact]
    public void ReadLogsPageIncludesTotalAndRequestStatus()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        for (var index = 0; index < 5; index++)
        {
            WriteBasicRequestLog(
                dbPath,
                ("request_id", $"req_{index}"),
                ("created_at", 100 + index),
                ("status_code", index == 4 ? 500 : 200),
                ("error", index == 4 ? "boom" : null));
        }

        var page = OpenCodexDatabase.ReadLogsPage(dbPath, page: 2, pageSize: 2);
        var parsed = OpenCodexDatabase.ReadLogsPage(dbPath, page: "bad", pageSize: 500);

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(["req_2", "req_1"], page.Events.Select(item => item.RequestId));
        Assert.Contains(page.Events[0].RequestStatus, new[] { "success", "failed" });
        Assert.Equal(1, parsed.Page);
        Assert.Equal(200, parsed.PageSize);
    }

    [Fact]
    public void ReadLogsPageOmitsLargeFieldsButDetailKeepsFullValues()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var requestHeaders = JsonSerializer.Serialize(Obj(("authorization", "Bearer " + new string('x', 80))));
        var requestBody = JsonSerializer.Serialize(Obj(("input", new string('a', 80))));
        var upstreamRequestBody = JsonSerializer.Serialize(Obj(("messages", List(Obj(("content", new string('u', 80)))))));
        var upstreamResponseBody = JsonSerializer.Serialize(Obj(("choices", List(Obj(("message", Obj(("content", new string('v', 80)))))))));
        var responseBody = JsonSerializer.Serialize(Obj(("output", new string('b', 80))));
        var webSearchJson = JsonSerializer.Serialize(Obj(("calls", List(Obj(("query", new string('c', 80)))))));
        var logId = WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_long"),
            ("request_headers", requestHeaders),
            ("request_body", requestBody),
            ("upstream_request_body", upstreamRequestBody),
            ("upstream_response_body", upstreamResponseBody),
            ("response_body", responseBody),
            ("web_search_json", webSearchJson));

        var page = OpenCodexDatabase.ReadLogsPage(dbPath);
        var eventProperties = typeof(RequestLogEventRecord).GetProperties().Select(property => property.Name).ToHashSet();
        var detail = OpenCodexDatabase.ReadLogById(dbPath, logId);

        var logEvent = Assert.Single(page.Events);
        Assert.Equal(logId, logEvent.Id);
        Assert.DoesNotContain("RequestHeaders", eventProperties);
        Assert.DoesNotContain("RequestBody", eventProperties);
        Assert.DoesNotContain("UpstreamRequestBody", eventProperties);
        Assert.DoesNotContain("UpstreamResponseBody", eventProperties);
        Assert.DoesNotContain("ResponseBody", eventProperties);
        Assert.DoesNotContain("WebSearchJson", eventProperties);
        Assert.NotNull(detail);
        Assert.Equal(requestHeaders, detail.RequestHeaders);
        Assert.Equal(requestBody, detail.RequestBody);
        Assert.Equal(upstreamRequestBody, detail.UpstreamRequestBody);
        Assert.Equal(upstreamResponseBody, detail.UpstreamResponseBody);
        Assert.Equal(responseBody, detail.ResponseBody);
        Assert.Equal(webSearchJson, detail.WebSearchJson);
    }

    [Fact]
    public void ReadLogFilterOptionsAreLoadedFromExistingLogs()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_200"),
            ("model", "gpt-4o"),
            ("upstream_model", "gpt-4o"),
            ("status_code", 200));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_502"),
            ("model", "claude-3-5-sonnet"),
            ("upstream_model", "claude-3-5-sonnet"),
            ("status_code", 502));

        var options = OpenCodexDatabase.ReadLogFilterOptions(dbPath);
        var modelOption = OpenCodexDatabase.ReadLogFilterOption(dbPath, "model", query: "gpt");
        var statusOption = OpenCodexDatabase.ReadLogFilterOption(dbPath, "status_code", query: "502");
        var statusValues = OpenCodexDatabase.ReadLogFilterOption(dbPath, "request_status");
        var unknown = OpenCodexDatabase.ReadLogFilterOption(dbPath, "unknown");

        Assert.Equal(["claude-3-5-sonnet", "gpt-4o"], Assert.IsAssignableFrom<IReadOnlyList<string>>(options["models"]));
        Assert.Equal([200L, 502L], Assert.IsAssignableFrom<IReadOnlyList<long>>(options["status_codes"]));
        Assert.Equal(["success", "failed"], Assert.IsAssignableFrom<IReadOnlyList<string>>(options["request_statuses"]));
        Assert.Equal(["gpt-4o"], Assert.IsAssignableFrom<IReadOnlyList<string>>(modelOption["models"]));
        Assert.Equal([502L], Assert.IsAssignableFrom<IReadOnlyList<long>>(statusOption["status_codes"]));
        Assert.Equal(["success", "failed"], Assert.IsAssignableFrom<IReadOnlyList<string>>(statusValues["request_statuses"]));
        Assert.Empty(unknown);
    }

    [Fact]
    public void ReadLogsFiltersByCommonFields()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        const double now = 2000.0;
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_ok"),
            ("created_at", now - 60),
            ("path", "/v1/responses"),
            ("client_ip", "127.0.0.1"),
            ("model", "gpt-4o"),
            ("upstream_model", "gpt-4o"),
            ("channel_id", "openai"),
            ("is_stream", 0),
            ("status_code", 200));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_error"),
            ("created_at", now),
            ("path", "/v1/chat/completions"),
            ("client_ip", "10.0.0.8"),
            ("model", "claude-3-5-sonnet"),
            ("upstream_model", "claude-3-5-sonnet"),
            ("channel_id", "anthropic"),
            ("is_stream", 1),
            ("ttft_ms", 20),
            ("duration_ms", 250),
            ("status_code", 502),
            ("output_tokens", 0),
            ("cost", 0.0),
            ("error", "upstream timeout"));

        var logs = OpenCodexDatabase.ReadLogs(
            dbPath,
            filters: Obj(
                ("channel_id", "anthropic"),
                ("model", "claude"),
                ("path", "/v1/chat/completions"),
                ("client_ip", "10.0.0"),
                ("status_code", 502),
                ("is_stream", 1),
                ("error", "timeout"),
                ("request_status", "failed"),
                ("created_from", now - 1)));
        var page = OpenCodexDatabase.ReadLogsPage(
            dbPath,
            filters: Obj(("created_to", now - 1), ("request_status", "success")));

        var log = Assert.Single(logs);
        Assert.Equal("req_error", log.RequestId);
        Assert.True(log.IsStream);
        Assert.Equal("failed", log.RequestStatus);
        Assert.Equal(["req_ok"], page.Events.Select(item => item.RequestId));
    }

    [Fact]
    public void ReadLogsFiltersByOwnerAndAccessKey()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var aliceId = WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_alice"),
            ("owner_username", "alice"),
            ("api_key_id", 7));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_bob"),
            ("owner_username", "bob"),
            ("api_key_id", 8));

        var logs = OpenCodexDatabase.ReadLogs(
            dbPath,
            filters: Obj(("owner_username", "alice"), ("api_key_id", 7)));
        var options = OpenCodexDatabase.ReadLogFilterOptions(
            dbPath,
            filters: Obj(("owner_username", "alice")));
        var allowedDetail = OpenCodexDatabase.ReadLogById(
            dbPath,
            aliceId,
            filters: Obj(("owner_username", "alice")));
        var blockedDetail = OpenCodexDatabase.ReadLogById(
            dbPath,
            aliceId,
            filters: Obj(("owner_username", "bob")));

        var log = Assert.Single(logs);
        Assert.Equal("req_alice", log.RequestId);
        Assert.Equal(["alice"], Assert.IsAssignableFrom<IReadOnlyList<string>>(options["owner_usernames"]));
        Assert.Equal([7L], Assert.IsAssignableFrom<IReadOnlyList<long>>(options["api_key_ids"]));
        Assert.NotNull(allowedDetail);
        Assert.Null(blockedDetail);
    }

    [Fact]
    public void AsyncRequestLogWriterWritesQueuedRecords()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        using var writer = new AsyncRequestLogWriter(dbPath);
        writer.Start();

        writer.Write(BasicRequestLog(("request_id", "test123")));
        writer.Stop();

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(dbPath));
        Assert.Equal("test123", log.RequestId);
        Assert.Equal("gpt-4o", log.Model);
        Assert.Equal(100, log.InputTokens);
    }

    [Fact]
    public void AsyncRequestLogWriterStopFlushesQueuedRecords()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        using var writer = new AsyncRequestLogWriter(dbPath);
        writer.Start();
        for (var index = 0; index < 50; index++)
        {
            writer.Write(BasicRequestLog(("request_id", $"req_{index}")));
        }

        writer.Stop();

        var logs = OpenCodexDatabase.ReadLogs(dbPath, limit: 100);
        Assert.Equal(50, logs.Count);
        Assert.Equal("req_49", logs[0].RequestId);
        Assert.Equal("req_0", logs[^1].RequestId);
    }

    [Fact]
    public void AsyncRequestLogWriterAllowsQueueingBeforeStart()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        using var writer = new AsyncRequestLogWriter(dbPath);

        writer.Write(BasicRequestLog(("request_id", "queued_before_start")));
        writer.Start();
        writer.Stop();

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(dbPath));
        Assert.Equal("queued_before_start", log.RequestId);
    }

    [Fact]
    public void AsyncRequestLogWriterStartIsIdempotentAndUsesDefaultOwner()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        using var writer = new AsyncRequestLogWriter(dbPath, defaultOwnerUsername: "root");
        writer.Start();
        writer.Start();

        writer.Write(BasicRequestLog(("request_id", "owned")));
        writer.Stop();

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(dbPath));
        Assert.Equal("owned", log.RequestId);
        Assert.Equal("root", log.OwnerUsername);
    }

    [Fact]
    public void ReadStatsCustomRangeSummarizesPointsAndModels()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        const double now = 1_700_003_600;
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req1"),
            ("created_at", now - 60),
            ("model", "m1"),
            ("upstream_model", "m1"),
            ("ttft_ms", 100),
            ("status_code", 200),
            ("input_tokens", 30),
            ("cached_tokens", 10),
            ("output_tokens", 20),
            ("cost", 7.25),
            ("owner_username", "admin"),
            ("error", null));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req2"),
            ("created_at", now - 120),
            ("model", "m1"),
            ("upstream_model", "m1"),
            ("ttft_ms", 0),
            ("status_code", 200),
            ("input_tokens", 40),
            ("cached_tokens", 20),
            ("output_tokens", 10),
            ("cost", 14.5),
            ("owner_username", "admin"),
            ("error", ""));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req3"),
            ("created_at", now - 4000),
            ("model", "m2"),
            ("upstream_model", "m2"),
            ("status_code", 500),
            ("input_tokens", 1),
            ("cached_tokens", 1),
            ("output_tokens", 1),
            ("cost", 10.0),
            ("owner_username", "admin"),
            ("error", "boom"));

        var stats = OpenCodexDatabase.ReadStats(
            dbPath,
            rangeKey: "custom",
            startTs: now - 3600,
            endTs: now);

        Assert.Equal("custom", stats.Range);
        Assert.Equal(1, stats.GranularityMinutes);
        Assert.Equal(7.25, stats.CurrencyRate);
        Assert.Equal("2023-11-15T06:13:20", stats.Start);
        Assert.Equal("2023-11-15T07:13:20", stats.End);
        Assert.Equal(60, stats.Points.Count);
        Assert.Equal(2, stats.Summary.RequestCount);
        Assert.Equal(2, stats.Summary.SuccessCount);
        Assert.Equal(2, stats.Summary.Recent1hRequestCount);
        Assert.Equal(70, stats.Summary.InputTokens);
        Assert.Equal(30, stats.Summary.CachedTokens);
        Assert.Equal(30, stats.Summary.OutputTokens);
        Assert.Equal(130, stats.Summary.TotalTokens);
        Assert.Equal(130, stats.Summary.Recent1hTokens);
        Assert.Equal(21.75, stats.Summary.Cost);
        Assert.Equal(21.75, stats.Summary.Recent1hCost);
        Assert.Equal(1, stats.Summary.Rpm);
        Assert.Equal(60, stats.Summary.Tpm);
        var model = Assert.Single(stats.ModelDistribution);
        Assert.Equal("m1", model.Model);
        Assert.Equal(2, model.Count);
        var activePoints = stats.Points.Where(point => point.Cost > 0).ToList();
        Assert.Equal(2, activePoints.Count);
        Assert.Equal("2023-11-15T07:12:20", activePoints[0].Time);
        Assert.Equal(14.5, activePoints[0].Cost);
        Assert.Equal(40, activePoints[0].InputTokens);
        Assert.Equal(20, activePoints[0].CachedTokens);
        Assert.Equal(10, activePoints[0].OutputTokens);
        Assert.Null(activePoints[0].AvgTtftMs);
        Assert.Equal(0.3333, activePoints[0].CacheHitRate);
        Assert.Equal(1, activePoints[0].Rpm);
        Assert.Equal("2023-11-15T07:13:20", activePoints[1].Time);
        Assert.Equal(7.25, activePoints[1].Cost);
        Assert.Equal(30, activePoints[1].InputTokens);
        Assert.Equal(10, activePoints[1].CachedTokens);
        Assert.Equal(20, activePoints[1].OutputTokens);
        Assert.Equal(100, activePoints[1].AvgTtftMs);
        Assert.Equal(0.25, activePoints[1].CacheHitRate);
        Assert.Equal(1, activePoints[1].Rpm);
    }

    [Fact]
    public void ReadStatsEmptyExistingDatabaseReturnsZeroPoints()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.Initialize(dbPath);

        var stats = OpenCodexDatabase.ReadStats(
            dbPath,
            rangeKey: "custom",
            startTs: 1_700_000_000,
            endTs: 1_700_000_300);

        Assert.Equal("custom", stats.Range);
        Assert.Equal(1, stats.GranularityMinutes);
        Assert.Equal(5, stats.Points.Count);
        Assert.All(stats.Points, point =>
        {
            Assert.Equal(0, point.Cost);
            Assert.Equal(0, point.InputTokens);
            Assert.Equal(0, point.CachedTokens);
            Assert.Equal(0, point.OutputTokens);
            Assert.Null(point.AvgTtftMs);
            Assert.Null(point.CacheHitRate);
            Assert.Equal(0, point.Rpm);
        });
        Assert.Empty(stats.ModelDistribution);
        Assert.Equal(0, stats.Summary.RequestCount);
        Assert.Equal(0, stats.Summary.TotalTokens);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 5)]
    [InlineData("24h", 15)]
    [InlineData("7d", 120)]
    [InlineData("30d", 720)]
    [InlineData("invalid", 1)]
    public void ReadStatsResolvesSupportedAndInvalidRanges(string range, int granularity)
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var stats = OpenCodexDatabase.ReadStats(dbPath, rangeKey: range);

        Assert.Equal(range == "invalid" ? "1h" : range, stats.Range);
        Assert.Equal(granularity, stats.GranularityMinutes);
        Assert.Empty(stats.Points);
        Assert.Empty(stats.ModelDistribution);
    }

    [Fact]
    public void ReadStatsScopesByOwner()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        const double now = 1_700_003_600;
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_alice"),
            ("created_at", now - 60),
            ("model", "m1"),
            ("owner_username", "alice"),
            ("input_tokens", 10),
            ("cached_tokens", 0),
            ("output_tokens", 5),
            ("cost", 1.5));
        WriteBasicRequestLog(
            dbPath,
            ("request_id", "req_bob"),
            ("created_at", now - 60),
            ("model", "m2"),
            ("owner_username", "bob"),
            ("input_tokens", 20),
            ("cached_tokens", 0),
            ("output_tokens", 5),
            ("cost", 2.5));

        var stats = OpenCodexDatabase.ReadStats(
            dbPath,
            rangeKey: "custom",
            startTs: now - 3600,
            endTs: now,
            ownerUsername: "alice");

        Assert.Equal(1, stats.Summary.RequestCount);
        Assert.Equal(15, stats.Summary.TotalTokens);
        Assert.Equal(1.5, stats.Summary.Cost);
        var model = Assert.Single(stats.ModelDistribution);
        Assert.Equal("m1", model.Model);
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void SuperadminIsEnvironmentAuthoritative()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "first");
        Assert.NotNull(OpenCodexDatabase.AuthenticateUser(dbPath, "root", "first"));

        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "second");

        var user = OpenCodexDatabase.AuthenticateUser(dbPath, "root", "second");
        Assert.NotNull(user);
        Assert.Equal("superadmin", user.Role);
        Assert.Null(OpenCodexDatabase.AuthenticateUser(dbPath, "root", "first"));
    }

    [Fact]
    public void PasswordHashUsesPythonCompatiblePbkdf2Format()
    {
        var passwordHash = OpenCodexDatabase.HashPassword("secret");

        var parts = passwordHash.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2_sha256", parts[0]);
        Assert.Equal("200000", parts[1]);
        Assert.Equal(32, parts[2].Length);
        Assert.Equal(64, parts[3].Length);
        Assert.True(OpenCodexDatabase.VerifyPassword("secret", passwordHash));
        Assert.False(OpenCodexDatabase.VerifyPassword("wrong", passwordHash));
        Assert.False(OpenCodexDatabase.VerifyPassword("secret", "not-a-valid-hash"));
    }

    [Fact]
    public void AccessApiKeyPlaintextIsStoredForCopyingAndHashIsKept()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "pw");
        OpenCodexDatabase.CreateUser(dbPath, "alice", "alice-pw");

        var created = OpenCodexDatabase.CreateAccessApiKey(dbPath, "alice", "Laptop");
        var rawKey = created.Key;

        Assert.NotNull(rawKey);
        Assert.StartsWith("ocx_", rawKey);
        Assert.Equal("alice", created.OwnerUsername);
        var listed = OpenCodexDatabase.ListAccessApiKeys(dbPath, "alice");
        var listedKey = Assert.Single(listed);
        Assert.Equal(rawKey, listedKey.Key);
        Assert.Equal(created.MaskedKey, listedKey.MaskedKey);

        using (var connection = OpenConnection(dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key_hash, key_prefix, key_suffix, key_plaintext FROM access_api_keys WHERE id = $id";
            command.Parameters.AddWithValue("$id", created.Id);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(64, reader.GetString(0).Length);
            Assert.NotEqual(rawKey, reader.GetString(0));
            Assert.Equal(rawKey[..12], reader.GetString(1));
            Assert.Equal(rawKey[^6..], reader.GetString(2));
            Assert.Equal(rawKey, reader.GetString(3));
        }

        var authenticated = OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, rawKey);

        Assert.NotNull(authenticated);
        Assert.Equal("alice", authenticated.User.Username);
        Assert.NotNull(authenticated.LastUsedAt);
    }

    [Fact]
    public void AccessApiKeyLegacyRowsWithoutPlaintextAreListedWithoutCopyValue()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "pw");
        OpenCodexDatabase.CreateUser(dbPath, "alice", "alice-pw");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        const string rawKey = "ocx_legacy-secret";

        using (var connection = OpenConnection(dbPath))
        {
            ExecuteNonQuery(
                connection,
                $$"""
                INSERT INTO access_api_keys (
                    owner_username, name, key_hash, key_prefix, key_suffix,
                    enabled, created_at, updated_at
                ) VALUES (
                    'alice', 'Legacy', '{{new string('0', 64)}}',
                    '{{rawKey[..12]}}', '{{rawKey[^6..]}}', 1, {{now}}, {{now}}
                )
                """);
        }

        var listed = OpenCodexDatabase.ListAccessApiKeys(dbPath, "alice");

        var key = Assert.Single(listed);
        Assert.Null(key.Key);
    }

    [Fact]
    public void DisabledOrDeletedAccessApiKeyIsRejected()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "pw");
        OpenCodexDatabase.CreateUser(dbPath, "alice", "alice-pw");
        var created = OpenCodexDatabase.CreateAccessApiKey(dbPath, "alice", "Laptop");
        Assert.NotNull(created.Key);

        OpenCodexDatabase.SetAccessApiKeyEnabled(dbPath, created.Id, enabled: false);
        Assert.Null(OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, created.Key));

        OpenCodexDatabase.SetAccessApiKeyEnabled(dbPath, created.Id, enabled: true);
        Assert.NotNull(OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, created.Key));

        OpenCodexDatabase.DeleteAccessApiKey(dbPath, created.Id);
        Assert.Null(OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, created.Key));
    }

    [Fact]
    public void DisabledUserAccessApiKeyIsRejected()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "pw");
        OpenCodexDatabase.CreateUser(dbPath, "alice", "alice-pw");
        var created = OpenCodexDatabase.CreateAccessApiKey(dbPath, "alice", "Laptop");
        Assert.NotNull(created.Key);

        OpenCodexDatabase.SetUserEnabled(dbPath, "alice", enabled: false);

        Assert.Null(OpenCodexDatabase.AuthenticateUser(dbPath, "alice", "alice-pw"));
        Assert.Null(OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, created.Key));
    }

    [Fact]
    public void DeleteUserRemovesOwnedApiKeysAndChannelsButNotCurrentUser()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.EnsureSuperadmin(dbPath, "root", "pw");
        OpenCodexDatabase.CreateUser(dbPath, "alice", "alice-pw");
        var created = OpenCodexDatabase.CreateAccessApiKey(dbPath, "alice", "Laptop");
        OpenCodexDatabase.ReplaceChannels(
            dbPath,
            [Obj(("id", "chat"), ("type", "chat"), ("baseurl", "https://alice.example.test/v1"))],
            ownerUsername: "alice",
            defaultOwnerUsername: "root");
        Assert.NotNull(created.Key);

        var deleted = OpenCodexDatabase.DeleteUser(dbPath, "alice", protectedUsername: "root");

        Assert.Equal("alice", deleted.Username);
        Assert.Null(OpenCodexDatabase.AuthenticateUser(dbPath, "alice", "alice-pw"));
        Assert.Null(OpenCodexDatabase.AuthenticateAccessApiKey(dbPath, created.Key));
        Assert.Empty(OpenCodexDatabase.ListAccessApiKeys(dbPath, "alice"));
        Assert.Empty(OpenCodexDatabase.ReadChannels(dbPath, ownerUsername: "alice"));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenCodexDatabase.DeleteUser(dbPath, "root", protectedUsername: "root"));
        Assert.Equal("cannot delete current user", exception.Message);
    }

    [Fact]
    public void SaveAndReadWebSearchConfigKeepsFullKeysAndOrder()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(
                    Obj(("provider", "tavily"), ("key", "tvly-first"), ("enabled", true), ("usage_limit", 2)),
                    Obj(("provider", "tavily"), ("key", "tvly-second"), ("enabled", false), ("usage_limit", 3))))));

        Assert.True(saved.Enabled);
        Assert.Equal(["tavily"], saved.Providers);
        Assert.Equal(1000, saved.DefaultKeyUsageLimit);
        Assert.Equal(["tvly-first", "tvly-second"], saved.Keys.Select(item => item.Key));
        Assert.Equal(["tavily", "tavily"], saved.Keys.Select(item => item.Provider));
        Assert.Equal([2, 3], saved.Keys.Select(item => item.UsageLimit));
        Assert.Equal([0, 0], saved.Keys.Select(item => item.UsageCount));
        Assert.False(saved.Keys[1].Enabled);

        var loaded = OpenCodexDatabase.ReadWebSearchConfig(dbPath);

        Assert.Equal(["tvly-first", "tvly-second"], loaded.Keys.Select(item => item.Key));
        Assert.Equal([2, 3], loaded.Keys.Select(item => item.UsageLimit));
    }

    [Fact]
    public void WebSearchConfigAllowsLegacyDefaultUsageLimit()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("key_usage_limit", 2),
                ("keys", List(Obj(("key", "tvly-first"), ("enabled", true))))));

        Assert.Equal(2, saved.Keys[0].KeyUsageLimit);
        Assert.Equal(2, saved.Keys[0].UsageLimit);

        var loaded = OpenCodexDatabase.ReadWebSearchConfig(dbPath);

        Assert.Equal(2, loaded.Keys[0].KeyUsageLimit);
        Assert.Equal(2, loaded.Keys[0].UsageLimit);
    }

    [Fact]
    public void WebSearchUsageLimitMustBePositiveInteger()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var exception = Assert.Throws<ArgumentException>(() =>
            OpenCodexDatabase.ReplaceWebSearchConfig(
                dbPath,
                Obj(
                    ("enabled", true),
                    ("keys", List(Obj(("key", "tvly-first"), ("enabled", true), ("usage_limit", 0)))))));

        Assert.Equal("web search keys[1].usage_limit must be a positive integer", exception.Message);
    }

    [Fact]
    public void WebSearchProviderMustBeSupported()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var exception = Assert.Throws<ArgumentException>(() =>
            OpenCodexDatabase.ReplaceWebSearchConfig(
                dbPath,
                Obj(
                    ("enabled", true),
                    ("keys", List(Obj(("provider", "other"), ("key", "search-key"), ("enabled", true)))))));

        Assert.Equal("unsupported web search provider: other", exception.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData("1.5")]
    [InlineData(true)]
    public void WebSearchUsageCountMustBeNonNegativeInteger(object value)
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;

        var exception = Assert.Throws<ArgumentException>(() =>
            OpenCodexDatabase.ReplaceWebSearchConfig(
                dbPath,
                Obj(
                    ("enabled", true),
                    ("keys", List(Obj(("key", "tvly-first"), ("enabled", true), ("usage_count", value)))))));

        Assert.Equal("web search keys[1].usage_count must be a non-negative integer", exception.Message);
    }

    [Fact]
    public void ReserveUsesEnabledKeysByPositionAndCountsOnRequestStart()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(
                    Obj(("key", "disabled"), ("enabled", false)),
                    Obj(("key", "first"), ("enabled", true)),
                    Obj(("key", "second"), ("enabled", true))))));

        var reserved = OpenCodexDatabase.ReserveTavilyKey(dbPath);

        Assert.NotNull(reserved);
        Assert.Equal("first", reserved.Key);
        Assert.Equal(1, reserved.Position);
        Assert.Equal(1, reserved.UsageCount);
        var config = OpenCodexDatabase.ReadWebSearchConfig(dbPath);
        Assert.Equal([0, 1, 0], config.Keys.Select(item => item.UsageCount));
    }

    [Fact]
    public void ReserveSwitchesToNextKeyAfterUsageLimit()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(
                    Obj(("key", "first"), ("enabled", true), ("usage_limit", 1)),
                    Obj(("key", "second"), ("enabled", true))))));
        var firstId = saved.Keys[0].Id;
        using (var connection = OpenConnection(dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tavily_keys SET usage_count = $usage_count WHERE id = $id";
            command.Parameters.AddWithValue("$usage_count", 1);
            command.Parameters.AddWithValue("$id", firstId);
            command.ExecuteNonQuery();
        }

        var reserved = OpenCodexDatabase.ReserveTavilyKey(dbPath);

        Assert.NotNull(reserved);
        Assert.Equal("second", reserved.Key);
        Assert.Equal(1, reserved.UsageCount);
        Assert.Equal(1000, reserved.KeyUsageLimit);
    }

    [Fact]
    public void ReserveSwitchesToNextKeyAfterPerKeyUsageLimit()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(
                    Obj(("key", "first"), ("enabled", true), ("usage_limit", 2)),
                    Obj(("key", "second"), ("enabled", true), ("usage_limit", 5))))));

        var first = OpenCodexDatabase.ReserveTavilyKey(dbPath);
        var second = OpenCodexDatabase.ReserveTavilyKey(dbPath);
        var third = OpenCodexDatabase.ReserveTavilyKey(dbPath);

        Assert.Equal(2, saved.Keys[0].UsageLimit);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal("first", first.Key);
        Assert.Equal(2, first.KeyUsageLimit);
        Assert.Equal("first", second.Key);
        Assert.Equal(2, second.UsageCount);
        Assert.Equal("second", third.Key);
        Assert.Equal(5, third.KeyUsageLimit);
    }

    [Fact]
    public void TestReserveCanUseDisabledKeyButNotExhaustedKey()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(
                    Obj(("key", "disabled"), ("enabled", false)),
                    Obj(("key", "exhausted"), ("enabled", true), ("usage_limit", 1))))));
        var disabledId = saved.Keys[0].Id;
        var exhaustedId = saved.Keys[1].Id;
        using (var connection = OpenConnection(dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tavily_keys SET usage_count = $usage_count WHERE id = $id";
            command.Parameters.AddWithValue("$usage_count", 1);
            command.Parameters.AddWithValue("$id", exhaustedId);
            command.ExecuteNonQuery();
        }

        var disabled = OpenCodexDatabase.ReserveTavilyKeyById(dbPath, disabledId);
        var exhausted = OpenCodexDatabase.ReserveTavilyKeyById(dbPath, exhaustedId);

        Assert.NotNull(disabled);
        Assert.Equal("disabled", disabled.Key);
        Assert.Null(exhausted);
    }

    [Fact]
    public void KeyStringChangeResetsUsageButEnabledChangePreservesUsage()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(Obj(("provider", "tavily"), ("key", "same"), ("enabled", true), ("usage_limit", 10))))));
        var keyId = saved.Keys[0].Id;
        OpenCodexDatabase.ReserveTavilyKey(dbPath);

        var toggled = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(Obj(("id", keyId), ("provider", "tavily"), ("key", "same"), ("enabled", false), ("usage_limit", 20))))));
        var changed = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(Obj(("id", keyId), ("provider", "tavily"), ("key", "changed"), ("enabled", true), ("usage_limit", 20))))));

        Assert.Equal(1, toggled.Keys[0].UsageCount);
        Assert.Equal(20, toggled.Keys[0].UsageLimit);
        Assert.Equal(0, changed.Keys[0].UsageCount);
    }

    [Fact]
    public void KeyProviderCaseNormalizesAndPreservesUsage()
    {
        using var workspace = new TempWorkspace();
        var dbPath = workspace.DatabasePath;
        var saved = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(Obj(("provider", "tavily"), ("key", "same"), ("enabled", true))))));
        var keyId = saved.Keys[0].Id;
        OpenCodexDatabase.ReserveTavilyKey(dbPath);

        var changed = OpenCodexDatabase.ReplaceWebSearchConfig(
            dbPath,
            Obj(
                ("enabled", true),
                ("keys", List(Obj(("id", keyId), ("provider", "TAVILY"), ("key", "same"), ("enabled", true))))));

        Assert.Equal("tavily", changed.Keys[0].Provider);
        Assert.Equal(1, changed.Keys[0].UsageCount);
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static List<object?> List(params object?[] values)
    {
        return values.ToList();
    }

    private static long WriteBasicRequestLog(string dbPath, params (string Key, object? Value)[] overrides)
    {
        return OpenCodexDatabase.WriteRequestLog(dbPath, BasicRequestLog(overrides));
    }

    private static Dictionary<string, object?> BasicRequestLog(params (string Key, object? Value)[] overrides)
    {
        var record = Obj(
            ("request_id", "req"),
            ("created_at", 1000.0),
            ("method", "POST"),
            ("path", "/v1/responses"),
            ("client_ip", "127.0.0.1"),
            ("request_headers", "{}"),
            ("request_body", "{}"),
            ("model", "gpt-4o"),
            ("upstream_model", "gpt-4o"),
            ("channel_id", "openai"),
            ("is_stream", 0),
            ("ttft_ms", null),
            ("duration_ms", 100),
            ("status_code", 200),
            ("upstream_response_body", "{}"),
            ("response_body", "{}"),
            ("input_tokens", 100),
            ("cached_tokens", 0),
            ("output_tokens", 50),
            ("cost", 0.001),
            ("error", null));
        foreach (var (key, value) in overrides)
        {
            record[key] = value;
        }

        return record;
    }

    private static IReadOnlyDictionary<string, object?> AsObject(object? value)
    {
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value);
    }

    private static IReadOnlyList<object?> AsList(object? value)
    {
        return Assert.IsAssignableFrom<IReadOnlyList<object?>>(value);
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadNames(SqliteConnection connection, string commandText, int ordinal = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(ordinal));
        }

        return result;
    }

    private static List<string> ReadChannelPrimaryKey(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(channels)";
        using var reader = command.ExecuteReader();
        var result = new List<(int Position, string Name)>();
        while (reader.Read())
        {
            var primaryKeyPosition = reader.GetInt32(5);
            if (primaryKeyPosition > 0)
            {
                result.Add((primaryKeyPosition, reader.GetString(1)));
            }
        }

        return result.OrderBy(item => item.Position).Select(item => item.Name).ToList();
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"opencodex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "test.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
