using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests;

public sealed class AdminDataControllerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly FakeUpstreamClient _upstream = new();
    private readonly FakeWebSearchClient _webSearch = new();
    private readonly FakeUpstreamModelClient _models = new();
    private readonly HttpClient _client;

    public AdminDataControllerTests()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenCodex:DbPath", _workspace.DatabasePath);
            builder.UseSetting("OpenCodex:AdminUsername", "admin");
            builder.UseSetting("OpenCodex:AdminPassword", "pw");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.RemoveAll<IWebSearchClient>();
                services.RemoveAll<IUpstreamModelClient>();
                services.AddSingleton<IUpstreamClient>(_upstream);
                services.AddSingleton<IWebSearchClient>(_webSearch);
                services.AddSingleton<IUpstreamModelClient>(_models);
            });
        });
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AdminApiLoginSessionAndLogoutMatchPythonShape()
    {
        var initial = await GetJson("/admin/api/session");

        Assert.False(initial.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initial.RootElement.GetProperty("user").ValueKind);

        var login = await LoginAs("admin", "pw");

        Assert.True(login.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", login.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("superadmin", login.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.True(login.RootElement.GetProperty("user").GetProperty("enabled").GetBoolean());

        var session = await GetJson("/admin/api/session");
        Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", session.RootElement.GetProperty("user").GetProperty("username").GetString());

        var logoutResponse = await _client.PostAsync("/admin/api/logout", content: null);
        var logout = await ReadJson(logoutResponse);

        Assert.False(logout.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task AdminApiLoginRejectsInvalidCredentials()
    {
        var response = await _client.PostAsJsonAsync(
            "/admin/api/login",
            new { username = "admin", password = "wrong" });
        var payload = await ReadJson(response, HttpStatusCode.Unauthorized);

        Assert.Equal("用户名或密码错误", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AdminDataEndpointsRequireAuthentication()
    {
        var response = await _client.GetAsync("/admin/api/logs");
        var payload = await ReadJson(response, HttpStatusCode.Unauthorized);

        var error = payload.RootElement.GetProperty("error");
        Assert.Equal("admin authentication required", error.GetProperty("message").GetString());
        Assert.Equal("bad_request", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task LogsEndpointReturnsPagedSnakeCaseEvents()
    {
        await LoginAsAdmin();
        WriteLog(("request_id", "req_old"), ("created_at", 1000.0));
        WriteLog(("request_id", "req_new"), ("created_at", 1001.0), ("status_code", 500), ("error", "boom"));

        var payload = await GetJson("/admin/api/logs?page=1&page_size=1&request_status=failed");

        Assert.Equal(1, payload.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("page_size").GetInt32());
        var item = payload.RootElement.GetProperty("events")[0];
        Assert.Equal("req_new", item.GetProperty("request_id").GetString());
        Assert.Equal("failed", item.GetProperty("request_status").GetString());
        Assert.Equal("gpt-4o", item.GetProperty("upstream_model").GetString());
        Assert.False(item.TryGetProperty("request_body", out _));
    }

    [Fact]
    public async Task LogFilterOptionsEndpointReturnsSingleOptionSet()
    {
        await LoginAsAdmin();
        WriteLog(("request_id", "req_gpt"), ("model", "gpt-4o"), ("status_code", 200));
        WriteLog(("request_id", "req_claude"), ("model", "claude-3-5-sonnet"), ("status_code", 502));

        var payload = await GetJson("/admin/api/log-filter-options?field=model&q=gpt");

        var models = payload.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Equal(["gpt-4o"], models);
    }

    [Fact]
    public async Task LogDetailEndpointIncludesLargeFieldsAnd404sMissingRows()
    {
        await LoginAsAdmin();
        var id = WriteLog(
            ("request_id", "req_detail"),
            ("request_body", """{"input":"raw"}"""),
            ("response_body", """{"output":"final"}"""),
            ("web_search_json", """{"calls":[{"query":"OpenAI"}]}"""));

        var payload = await GetJson($"/admin/api/logs/{id}");
        var missing = await _client.GetAsync("/admin/api/logs/999999");

        Assert.Equal("req_detail", payload.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("""{"input":"raw"}""", payload.RootElement.GetProperty("request_body").GetString());
        Assert.Equal("""{"output":"final"}""", payload.RootElement.GetProperty("response_body").GetString());
        Assert.Equal("""{"calls":[{"query":"OpenAI"}]}""", payload.RootElement.GetProperty("web_search_json").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingPayload = await missing.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(missingPayload);
        Assert.Equal("log not found", missingPayload["error"]);
    }

    [Fact]
    public async Task StatsEndpointReturnsDashboardShape()
    {
        await LoginAsAdmin();
        const double now = 1_700_003_600;
        WriteLog(
            ("request_id", "req1"),
            ("created_at", now - 60),
            ("model", "m1"),
            ("input_tokens", 30),
            ("cached_tokens", 10),
            ("output_tokens", 20),
            ("cost", 7.25));
        WriteLog(
            ("request_id", "req2"),
            ("created_at", now - 120),
            ("model", "m1"),
            ("input_tokens", 40),
            ("cached_tokens", 20),
            ("output_tokens", 10),
            ("cost", 14.5));

        var payload = await GetJson($"/admin/api/stats?range=custom&start={now - 3600}&end={now}");

        Assert.Equal("custom", payload.RootElement.GetProperty("range").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("granularity_minutes").GetInt32());
        Assert.Equal(7.25, payload.RootElement.GetProperty("currency_rate").GetDouble());
        var summary = payload.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("request_count").GetInt32());
        Assert.Equal(130, summary.GetProperty("total_tokens").GetInt32());
        Assert.Equal(21.75, summary.GetProperty("cost").GetDouble());
        Assert.Equal(60, payload.RootElement.GetProperty("points").GetArrayLength());
        var model = payload.RootElement.GetProperty("model_distribution")[0];
        Assert.Equal("m1", model.GetProperty("model").GetString());
        Assert.Equal(2, model.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task UsersEndpointCreatesListsUpdatesAndDeletesUsers()
    {
        await LoginAsAdmin();
        var createdResponse = await _client.PostAsJsonAsync(
            "/admin/api/users",
            new { username = "alice", password = "alice-pw", enabled = true });
        var created = await ReadJson(createdResponse, HttpStatusCode.Created);

        Assert.Equal("alice", created.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("user", created.RootElement.GetProperty("user").GetProperty("role").GetString());

        var listed = await GetJson("/admin/api/users");
        var users = listed.RootElement.GetProperty("users").EnumerateArray().ToList();
        Assert.Contains(users, item => item.GetProperty("username").GetString() == "alice");

        var patchedResponse = await _client.PatchAsJsonAsync(
            "/admin/api/users/alice",
            new { enabled = false });
        var patched = await ReadJson(patchedResponse);

        Assert.False(patched.RootElement.GetProperty("user").GetProperty("enabled").GetBoolean());
        Assert.Null(OpenCodexDatabase.AuthenticateUser(_workspace.DatabasePath, "alice", "alice-pw"));

        var missingResponse = await _client.PatchAsJsonAsync(
            "/admin/api/users/missing",
            new { enabled = true });
        var missing = await ReadJson(missingResponse, HttpStatusCode.NotFound);
        Assert.Equal("user not found", missing.RootElement.GetProperty("error").GetString());

        var deletedResponse = await _client.DeleteAsync("/admin/api/users/alice");
        var deleted = await ReadJson(deletedResponse);

        Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Equal("alice", deleted.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task ApiKeysEndpointCreatesListsDisablesAndDeletesKeys()
    {
        await LoginAsAdmin();
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");

        var createdResponse = await _client.PostAsJsonAsync(
            "/admin/api/api-keys",
            new { owner_username = "alice", name = "Laptop" });
        var created = await ReadJson(createdResponse, HttpStatusCode.Created);
        var key = created.RootElement.GetProperty("key");
        var keyId = key.GetProperty("id").GetInt64();

        Assert.Equal("alice", key.GetProperty("owner_username").GetString());
        Assert.Equal("Laptop", key.GetProperty("name").GetString());
        Assert.StartsWith("ocx_", key.GetProperty("key").GetString());
        Assert.Contains("...", key.GetProperty("masked_key").GetString());

        var listed = await GetJson("/admin/api/api-keys?owner_username=alice");
        var listedKey = listed.RootElement.GetProperty("keys")[0];
        Assert.Equal(keyId, listedKey.GetProperty("id").GetInt64());
        Assert.Equal(key.GetProperty("key").GetString(), listedKey.GetProperty("key").GetString());

        var patchedResponse = await _client.PatchAsJsonAsync(
            $"/admin/api/api-keys/{keyId}",
            new { enabled = false });
        var patched = await ReadJson(patchedResponse);
        Assert.False(patched.RootElement.GetProperty("key").GetProperty("enabled").GetBoolean());

        var deletedResponse = await _client.DeleteAsync($"/admin/api/api-keys/{keyId}");
        var deleted = await ReadJson(deletedResponse);
        Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());

        var missingResponse = await _client.DeleteAsync($"/admin/api/api-keys/{keyId}");
        var missing = await ReadJson(missingResponse, HttpStatusCode.NotFound);
        Assert.Equal("api key not found", missing.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfigEndpointSavesAndReadsChannels()
    {
        await LoginAsAdmin();
        var savedResponse = await _client.PostAsJsonAsync(
            "/admin/api/config",
            new
            {
                channels = new object[]
                {
                    new
                    {
                        id = "chat",
                        name = "Chat",
                        type = "chat",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        headers = new Dictionary<string, object?> { ["X-Test"] = "yes" },
                        models = new object[]
                        {
                            new { model = "gpt-test" }
                        }
                    }
                }
            });
        var saved = await ReadJson(savedResponse);

        var channel = saved.RootElement.GetProperty("channels")[0];
        Assert.Equal("admin", channel.GetProperty("owner_username").GetString());
        Assert.Equal("chat", channel.GetProperty("id").GetString());
        Assert.Equal("Chat", channel.GetProperty("name").GetString());
        Assert.Equal("https://example.test/v1", channel.GetProperty("baseurl").GetString());
        Assert.Equal("secret", channel.GetProperty("apikey").GetString());
        Assert.Equal(120, channel.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal(3, channel.GetProperty("retry_count").GetInt32());
        Assert.True(channel.GetProperty("enabled").GetBoolean());
        Assert.Equal("yes", channel.GetProperty("headers").GetProperty("X-Test").GetString());
        Assert.Equal("gpt-test", channel.GetProperty("models")[0].GetProperty("upstream_model").GetString());

        var loaded = await GetJson("/admin/api/config");
        Assert.Equal("chat", loaded.RootElement.GetProperty("channels")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AdminConfiguredAccessKeyCanCallProxyAndViewPersistedLog()
    {
        await LoginAsAdmin();
        var keyResponse = await _client.PostAsJsonAsync(
            "/admin/api/api-keys",
            new { name = "Migration E2E" });
        var keyPayload = await ReadJson(keyResponse, HttpStatusCode.Created);
        var key = keyPayload.RootElement.GetProperty("key");
        var keyId = key.GetProperty("id").GetInt64();
        var accessKey = key.GetProperty("key").GetString();
        Assert.StartsWith("ocx_", accessKey);

        var savedConfigResponse = await _client.PostAsJsonAsync(
            "/admin/api/config",
            new
            {
                channels = new object[]
                {
                    new
                    {
                        id = "migration-e2e-chat",
                        name = "Migration E2E Chat",
                        type = "chat",
                        baseurl = "https://upstream.example.test/v1",
                        apikey = "upstream-secret",
                        auth_mode = "config",
                        models = new object[]
                        {
                            new { model = "client-model", upstream_model = "upstream-model" }
                        }
                    }
                }
            });
        var savedConfig = await ReadJson(savedConfigResponse);
        Assert.Equal(
            "migration-e2e-chat",
            savedConfig.RootElement.GetProperty("channels")[0].GetProperty("id").GetString());

        _upstream.Response = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_migration_e2e",
            ["model"] = "upstream-model",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "migration e2e ok"
                    },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 11,
                ["prompt_tokens_details"] = new Dictionary<string, object?>
                {
                    ["cached_tokens"] = 3
                },
                ["completion_tokens"] = 7,
                ["total_tokens"] = 18
            }
        };

        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessKey}");
        var proxyResponse = await _client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "client-model", input = "migration ping" });
        var proxyPayload = await ReadJson(proxyResponse);

        Assert.Equal("client-model", proxyPayload.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "migration e2e ok",
            proxyPayload.RootElement.GetProperty("output")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString());

        var upstreamCall = Assert.Single(_upstream.Calls);
        Assert.Equal("migration-e2e-chat", upstreamCall.Channel["id"]);
        Assert.Equal("upstream-model", upstreamCall.Payload["model"]);
        Assert.True(upstreamCall.Payload.ContainsKey("messages"));

        var logs = await GetJson("/admin/api/logs?page=1&page_size=10");
        Assert.Equal(1, logs.RootElement.GetProperty("total").GetInt32());
        var logEvent = logs.RootElement.GetProperty("events")[0];
        Assert.Equal("admin", logEvent.GetProperty("owner_username").GetString());
        Assert.Equal(keyId, logEvent.GetProperty("api_key_id").GetInt64());
        Assert.Equal("client-model", logEvent.GetProperty("model").GetString());
        Assert.Equal("upstream-model", logEvent.GetProperty("upstream_model").GetString());
        Assert.Equal("migration-e2e-chat", logEvent.GetProperty("channel_id").GetString());
        Assert.Equal(200, logEvent.GetProperty("status_code").GetInt32());
        Assert.Equal("success", logEvent.GetProperty("request_status").GetString());
        Assert.Equal(11, logEvent.GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, logEvent.GetProperty("cached_tokens").GetInt32());
        Assert.Equal(7, logEvent.GetProperty("output_tokens").GetInt32());

        var detail = await GetJson($"/admin/api/logs/{logEvent.GetProperty("id").GetInt64()}");
        Assert.Contains("migration ping", detail.RootElement.GetProperty("request_body").GetString(), StringComparison.Ordinal);
        Assert.Contains("migration ping", detail.RootElement.GetProperty("upstream_request_body").GetString(), StringComparison.Ordinal);
        Assert.Contains("migration e2e ok", detail.RootElement.GetProperty("upstream_response_body").GetString(), StringComparison.Ordinal);
        Assert.Contains("migration e2e ok", detail.RootElement.GetProperty("response_body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigExportIncludesFullApiKeyAndOnlyChannels()
    {
        await LoginAsAdmin();
        OpenCodexDatabase.ReplaceChannels(
            _workspace.DatabasePath,
            [new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = "chat",
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret"
            }],
            ownerUsername: "admin",
            defaultOwnerUsername: "admin");
        await _client.PostAsJsonAsync(
            "/admin/api/web-search",
            new
            {
                enabled = true,
                keys = new object[]
                {
                    new { key = "tvly-secret", enabled = true }
                }
            });

        var response = await _client.GetAsync("/admin/api/config/export");
        var rawPayload = await response.Content.ReadAsStringAsync();
        var payload = await ReadJson(response);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "opencodex-channels-config.json",
            response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains("\n  \"channels\": [", rawPayload, StringComparison.Ordinal);
        Assert.EndsWith("\n", rawPayload, StringComparison.Ordinal);
        Assert.Equal(
            ["channels"],
            payload.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("chat", payload.RootElement.GetProperty("channels")[0].GetProperty("id").GetString());
        Assert.Equal("secret", payload.RootElement.GetProperty("channels")[0].GetProperty("apikey").GetString());
        Assert.False(payload.RootElement.TryGetProperty("web_search", out _));
        Assert.False(payload.RootElement.TryGetProperty("tavily_keys", out _));
    }

    [Fact]
    public async Task ConfigImportAppendsWithoutOverwritingExistingIds()
    {
        await LoginAsAdmin();
        OpenCodexDatabase.ReplaceChannels(
            _workspace.DatabasePath,
            [new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = "chat",
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["timeout_seconds"] = 30
            }],
            ownerUsername: "admin",
            defaultOwnerUsername: "admin");

        var response = await _client.PostAsJsonAsync(
            "/admin/api/config/import",
            new
            {
                channels = new object[]
                {
                    new
                    {
                        id = "chat",
                        type = "chat",
                        baseurl = "https://duplicate.example.test/v1",
                        apikey = "changed",
                        auth_mode = "config",
                        timeout_seconds = 30
                    },
                    new
                    {
                        id = "messages",
                        type = "messages",
                        baseurl = "https://messages.example.test/v1",
                        apikey = "new-secret",
                        auth_mode = "config",
                        timeout_seconds = 45
                    }
                }
            });
        var payload = await ReadJson(response);

        Assert.Equal(1, payload.RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("skipped").GetInt32());
        Assert.Equal("chat", payload.RootElement.GetProperty("skipped_ids")[0].GetString());
        var channels = OpenCodexDatabase.ReadChannels(_workspace.DatabasePath, ownerUsername: "admin");
        Assert.Equal(["chat", "messages"], channels.Select(channel => channel.Id).ToArray());
        Assert.Equal("secret", channels[0].ApiKey);
        Assert.Equal("new-secret", channels[1].ApiKey);
    }

    [Fact]
    public async Task ConfigImportRejectsInvalidConfigWithoutChanges()
    {
        await LoginAsAdmin();
        OpenCodexDatabase.ReplaceChannels(
            _workspace.DatabasePath,
            [new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = "chat",
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret"
            }],
            ownerUsername: "admin",
            defaultOwnerUsername: "admin");

        var response = await _client.PostAsJsonAsync(
            "/admin/api/config/import",
            new
            {
                channels = new object[]
                {
                    new { id = "bad", type = "chat" }
                }
            });
        var payload = await ReadJson(response, HttpStatusCode.BadRequest);

        Assert.Contains("baseurl", payload.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        var channels = OpenCodexDatabase.ReadChannels(_workspace.DatabasePath, ownerUsername: "admin");
        Assert.Equal(["chat"], channels.Select(channel => channel.Id).ToArray());
        Assert.Equal("secret", channels[0].ApiKey);
    }

    [Fact]
    public async Task DiscoverModelsEndpointReturnsUniqueModelIds()
    {
        await LoginAsAdmin();
        _models.Response = new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = "gpt-4" },
                new Dictionary<string, object?> { ["id"] = "gpt-4" },
                new Dictionary<string, object?> { ["id"] = "gpt-4o" },
                new Dictionary<string, object?> { ["object"] = "model" }
            }
        };

        var response = await _client.PostAsJsonAsync(
            "/admin/api/channels/discover-models",
            new
            {
                channel = new
                {
                    id = "chat",
                    type = "chat",
                    baseurl = "https://example.test/v1",
                    apikey = "secret",
                    auth_mode = "config",
                    timeout_seconds = 30
                }
            });
        var payload = await ReadJson(response);

        var models = payload.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(["gpt-4", "gpt-4o"], models);
        Assert.Equal("list", payload.RootElement.GetProperty("raw").GetProperty("object").GetString());
        Assert.True(payload.RootElement.GetProperty("duration_ms").GetInt32() >= 0);

        var call = Assert.Single(_models.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("https://example.test/v1", call.Channel["baseurl"]);
        Assert.Equal(120, call.DefaultTimeout);
    }

    [Fact]
    public async Task DiscoverModelsEndpointMapsUpstreamErrorToBadGateway()
    {
        await LoginAsAdmin();
        _models.Exception = new UpstreamException(
            "upstream returned HTTP 401",
            401,
            new Dictionary<string, object?> { ["error"] = "unauthorized" },
            "chat");

        var response = await _client.PostAsJsonAsync(
            "/admin/api/discover-models",
            new
            {
                id = "chat",
                type = "chat",
                baseurl = "https://example.test/v1",
                apikey = "secret"
            });
        var payload = await ReadJson(response, HttpStatusCode.BadGateway);

        Assert.Equal("upstream returned HTTP 401", payload.RootElement.GetProperty("error").GetString());
        Assert.Equal(401, payload.RootElement.GetProperty("status_code").GetInt32());
        Assert.Equal("unauthorized", payload.RootElement.GetProperty("body").GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChannelTestEndpointRewritesModelMapping()
    {
        await LoginAsAdmin();
        _upstream.Response = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = "gpt-4",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "pong"
                    }
                }
            }
        };

        var response = await _client.PostAsJsonAsync(
            "/admin/api/channels/test",
            new
            {
                channel = new
                {
                    id = "chat",
                    type = "chat",
                    baseurl = "https://example.test/v1",
                    apikey = "secret",
                    auth_mode = "config",
                    timeout_seconds = 30,
                    models = new object[]
                    {
                        new { model = "gpt-5", upstream_model = "gpt-4" }
                    }
                },
                payload = new
                {
                    model = "gpt-5",
                    messages = new object[]
                    {
                        new { role = "user", content = "ping" }
                    }
                }
            });
        var payload = await ReadJson(response);

        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("gpt-5", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("gpt-4", payload.RootElement.GetProperty("upstream_model").GetString());
        Assert.Equal("gpt-5", payload.RootElement.GetProperty("response").GetProperty("model").GetString());
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("gpt-4", call.Payload["model"]);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal(120, call.DefaultTimeout);
    }

    [Fact]
    public async Task ChannelTestEndpointReturnsUpstreamErrorBodyWithOkFalse()
    {
        await LoginAsAdmin();
        _upstream.Exception = new UpstreamException(
            "upstream returned HTTP 400",
            400,
            new Dictionary<string, object?> { ["error"] = "bad model" },
            "chat");

        var response = await _client.PostAsJsonAsync(
            "/admin/api/test-channel",
            new
            {
                channel = new
                {
                    id = "chat",
                    type = "chat",
                    baseurl = "https://example.test/v1",
                    timeout_seconds = 30
                },
                payload = new
                {
                    model = "gpt-5",
                    messages = new object[]
                    {
                        new { role = "user", content = "ping" }
                    }
                }
            });
        var payload = await ReadJson(response);

        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(400, payload.RootElement.GetProperty("status_code").GetInt32());
        Assert.Equal("upstream returned HTTP 400", payload.RootElement.GetProperty("error").GetString());
        Assert.Equal("bad model", payload.RootElement.GetProperty("body").GetProperty("error").GetString());
    }

    [Fact]
    public async Task WebSearchEndpointSavesAndReadsKeys()
    {
        await LoginAsAdmin();
        var initial = await GetJson("/admin/api/web-search");

        Assert.False(initial.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("tavily", initial.RootElement.GetProperty("providers")[0].GetString());
        Assert.Equal(1000, initial.RootElement.GetProperty("default_key_usage_limit").GetInt32());
        Assert.False(initial.RootElement.TryGetProperty("key_usage_limit", out _));
        Assert.Equal(0, initial.RootElement.GetProperty("keys").GetArrayLength());

        var savedResponse = await _client.PostAsJsonAsync(
            "/admin/api/web-search",
            new
            {
                enabled = true,
                keys = new object[]
                {
                    new
                    {
                        provider = "tavily",
                        key = "tvly-a",
                        enabled = true,
                        usage_count = 3,
                        usage_limit = 250
                    },
                    new
                    {
                        provider = "tavily",
                        key = "tvly-b",
                        enabled = false,
                        usage_count = 8,
                        usage_limit = 500
                    }
                }
            });
        var saved = await ReadJson(savedResponse);

        Assert.True(saved.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(saved.RootElement.TryGetProperty("key_usage_limit", out _));
        var keys = saved.RootElement.GetProperty("keys");
        Assert.Equal(["tvly-a", "tvly-b"], keys.EnumerateArray().Select(item => item.GetProperty("key").GetString()!).ToArray());
        Assert.Equal([3, 8], keys.EnumerateArray().Select(item => item.GetProperty("usage_count").GetInt32()).ToArray());
        Assert.Equal([250, 500], keys.EnumerateArray().Select(item => item.GetProperty("usage_limit").GetInt32()).ToArray());
        Assert.Equal(250, keys[0].GetProperty("key_usage_limit").GetInt32());
        Assert.False(keys[1].GetProperty("enabled").GetBoolean());

        var loaded = await GetJson("/admin/api/web-search");
        Assert.Equal("tvly-a", loaded.RootElement.GetProperty("keys")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task WebSearchTestKeyAllowsDisabledKeyAndCountsUsage()
    {
        await LoginAsAdmin();
        var savedResponse = await _client.PostAsJsonAsync(
            "/admin/api/web-search",
            new
            {
                enabled = true,
                keys = new object[]
                {
                    new
                    {
                        provider = "tavily",
                        key = "tvly-disabled",
                        enabled = false
                    }
                }
            });
        var saved = await ReadJson(savedResponse);
        var keyId = saved.RootElement.GetProperty("keys")[0].GetProperty("id").GetInt64();
        _webSearch.Result = new WebSearchProviderResult(
            true,
            200,
            7,
            null,
            null,
            new WebSearchSummary("ok", [], null),
            new Dictionary<string, object?> { ["answer"] = "ok" });

        var response = await _client.PostAsJsonAsync(
            "/admin/api/web-search/test-key",
            new { id = keyId, query = "OpenAI" });
        var payload = await ReadJson(response);

        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("tavily", payload.RootElement.GetProperty("key").GetProperty("provider").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("key").GetProperty("usage_count").GetInt32());
        Assert.Equal(1000, payload.RootElement.GetProperty("key").GetProperty("usage_limit").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("config").GetProperty("keys")[0].GetProperty("usage_count").GetInt32());
        Assert.False(payload.RootElement.GetProperty("config").GetProperty("keys")[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("ok", payload.RootElement.GetProperty("result").GetProperty("summary").GetProperty("answer").GetString());

        var call = Assert.Single(_webSearch.Calls);
        Assert.Equal("tavily", call.Key.Provider);
        Assert.Equal("tvly-disabled", call.Key.Key);
        Assert.Equal("OpenAI", call.Query);
    }

    [Fact]
    public async Task WebSearchTestKeyUsesConfiguredUsageLimitMessage()
    {
        await LoginAsAdmin();
        var savedResponse = await _client.PostAsJsonAsync(
            "/admin/api/web-search",
            new
            {
                enabled = true,
                keys = new object[]
                {
                    new
                    {
                        provider = "tavily",
                        key = "tvly-limited",
                        enabled = true,
                        usage_limit = 1
                    }
                }
            });
        var saved = await ReadJson(savedResponse);
        var keyId = saved.RootElement.GetProperty("keys")[0].GetProperty("id").GetInt64();

        var first = await _client.PostAsJsonAsync(
            "/admin/api/web-search/test-key",
            new { id = keyId, query = "OpenAI" });
        var second = await _client.PostAsJsonAsync(
            "/admin/api/web-search/test-key",
            new { id = keyId, query = "OpenAI" });

        await ReadJson(first);
        var secondPayload = await ReadJson(second, HttpStatusCode.BadRequest);
        Assert.Contains("usage limit", secondPayload.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(_webSearch.Calls);
    }

    [Fact]
    public async Task RegularUserCannotUseSuperadminEndpoints()
    {
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        await LoginAs("alice", "alice-pw");

        var usersResponse = await _client.GetAsync("/admin/api/users");
        var usersPayload = await ReadJson(usersResponse, HttpStatusCode.Forbidden);
        var webSearchResponse = await _client.GetAsync("/admin/api/web-search");
        var webSearchPayload = await ReadJson(webSearchResponse, HttpStatusCode.Forbidden);
        var webSearchTestResponse = await _client.PostAsJsonAsync(
            "/admin/api/web-search/test-key",
            new { id = 1 });
        var webSearchTestPayload = await ReadJson(webSearchTestResponse, HttpStatusCode.Forbidden);

        Assert.Equal("superadmin required", usersPayload.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("superadmin required", webSearchPayload.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("superadmin required", webSearchTestPayload.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RegularUserApiKeyManagementIsScopedToSelf()
    {
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "bob", "bob-pw");
        var bobKey = OpenCodexDatabase.CreateAccessApiKey(_workspace.DatabasePath, "bob", "Bob");
        await LoginAs("alice", "alice-pw");

        var createdResponse = await _client.PostAsJsonAsync(
            "/admin/api/api-keys",
            new { owner_username = "bob", name = "Laptop" });
        var created = await ReadJson(createdResponse, HttpStatusCode.Created);
        var createdKey = created.RootElement.GetProperty("key");
        Assert.Equal("alice", createdKey.GetProperty("owner_username").GetString());

        var listed = await GetJson("/admin/api/api-keys?owner_username=bob");
        var keys = listed.RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.Single(keys);
        Assert.Equal("alice", keys[0].GetProperty("owner_username").GetString());

        var patchResponse = await _client.PatchAsJsonAsync(
            $"/admin/api/api-keys/{bobKey.Id}",
            new { enabled = false });
        var patchPayload = await ReadJson(patchResponse, HttpStatusCode.NotFound);

        Assert.Equal("api key not found", patchPayload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RegularUserConfigSaveForcesOwnOwner()
    {
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        OpenCodexDatabase.ReplaceChannels(
            _workspace.DatabasePath,
            [new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = "chat",
                ["baseurl"] = "https://admin.example.test/v1"
            }],
            ownerUsername: "admin",
            defaultOwnerUsername: "admin");
        await LoginAs("alice", "alice-pw");

        var response = await _client.PostAsJsonAsync(
            "/admin/api/config",
            new
            {
                channels = new object[]
                {
                    new
                    {
                        owner_username = "admin",
                        id = "chat",
                        type = "chat",
                        baseurl = "https://alice.example.test/v1",
                        timeout_seconds = 30
                    }
                }
            });
        var payload = await ReadJson(response);

        Assert.Equal("alice", payload.RootElement.GetProperty("channels")[0].GetProperty("owner_username").GetString());
        Assert.Equal(
            "https://alice.example.test/v1",
            OpenCodexDatabase.ReadChannels(_workspace.DatabasePath, ownerUsername: "alice")[0].BaseUrl);
        Assert.Equal(
            "https://admin.example.test/v1",
            OpenCodexDatabase.ReadChannels(_workspace.DatabasePath, ownerUsername: "admin")[0].BaseUrl);
    }

    [Fact]
    public async Task RegularUserLogsAndStatsAreScopedToSelf()
    {
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        var adminLogId = WriteLog(("request_id", "req_admin"), ("owner_username", "admin"), ("model", "admin-model"));
        WriteLog(("request_id", "req_alice"), ("owner_username", "alice"), ("model", "alice-model"));
        await LoginAs("alice", "alice-pw");

        var logs = await GetJson("/admin/api/logs?page=1&page_size=10");
        var events = logs.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(1, logs.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("alice", events[0].GetProperty("owner_username").GetString());
        Assert.Equal("alice-model", events[0].GetProperty("model").GetString());

        var options = await GetJson("/admin/api/log-filter-options?field=owner_username");
        Assert.Equal("alice", options.RootElement.GetProperty("owner_usernames")[0].GetString());

        var adminDetail = await _client.GetAsync($"/admin/api/logs/{adminLogId}");
        await ReadJson(adminDetail, HttpStatusCode.NotFound);

        var stats = await GetJson("/admin/api/stats?range=custom&start=1&end=2000");
        Assert.Equal(1, stats.RootElement.GetProperty("summary").GetProperty("request_count").GetInt32());
    }

    public void Dispose()
    {
        _client.Dispose();
        _workspace.Dispose();
    }

    private async Task<JsonDocument> GetJson(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<JsonDocument> ReadJson(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private Task<JsonDocument> LoginAsAdmin()
    {
        return LoginAs("admin", "pw");
    }

    private async Task<JsonDocument> LoginAs(string username, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/admin/api/login",
            new { username, password });
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var cookieHeader = string.Join("; ", setCookies.Select(cookie => cookie.Split(';', 2)[0]));
            _client.DefaultRequestHeaders.Remove("Cookie");
            _client.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        }

        return await ReadJson(response);
    }

    private long WriteLog(params (string Key, object? Value)[] overrides)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["request_id"] = "req",
            ["created_at"] = 1000.0,
            ["method"] = "POST",
            ["path"] = "/v1/responses",
            ["client_ip"] = "127.0.0.1",
            ["request_headers"] = "{}",
            ["request_body"] = "{}",
            ["model"] = "gpt-4o",
            ["upstream_model"] = "gpt-4o",
            ["channel_id"] = "openai",
            ["is_stream"] = 0,
            ["ttft_ms"] = null,
            ["duration_ms"] = 100,
            ["status_code"] = 200,
            ["upstream_response_body"] = "{}",
            ["response_body"] = "{}",
            ["input_tokens"] = 100,
            ["cached_tokens"] = 0,
            ["output_tokens"] = 50,
            ["cost"] = 0.001,
            ["error"] = null
        };
        foreach (var (key, value) in overrides)
        {
            record[key] = value;
        }

        return OpenCodexDatabase.WriteRequestLog(_workspace.DatabasePath, record);
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

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public List<UpstreamCall> Calls { get; } = [];

        public Dictionary<string, object?> Response { get; set; } = [];

        public UpstreamException? Exception { get; set; }

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));

            if (Exception is not null)
            {
                throw Exception;
            }

            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed record UpstreamCall(
        Dictionary<string, object?> Channel,
        Dictionary<string, object?> Payload,
        int DefaultTimeout);

    private sealed class FakeWebSearchClient : IWebSearchClient
    {
        public List<WebSearchCall> Calls { get; } = [];

        public WebSearchProviderResult Result { get; set; } = new(
            true,
            200,
            1,
            null,
            null,
            new WebSearchSummary("ok", [], null),
            new Dictionary<string, object?> { ["answer"] = "ok" });

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            Calls.Add(new WebSearchCall(key, query));
            return Task.FromResult(Result);
        }
    }

    private sealed record WebSearchCall(WebSearchProviderKey Key, string Query);

    private sealed class FakeUpstreamModelClient : IUpstreamModelClient
    {
        public List<ModelDiscoveryCall> Calls { get; } = [];

        public Dictionary<string, object?> Response { get; set; } = [];

        public UpstreamException? Exception { get; set; }

        public Task<Dictionary<string, object?>> ListModelsAsync(
            IReadOnlyDictionary<string, object?> channel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new ModelDiscoveryCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Response);
        }
    }

    private sealed record ModelDiscoveryCall(
        Dictionary<string, object?> Channel,
        int DefaultTimeout);
}
