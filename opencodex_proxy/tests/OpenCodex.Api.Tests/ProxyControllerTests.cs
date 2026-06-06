using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

public sealed class ProxyControllerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly FakeUpstreamClient _upstream = new();
    private readonly FakeWebSearchClient _webSearch = new();
    private readonly HttpClient _client;

    public ProxyControllerTests()
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
                services.AddSingleton<IUpstreamClient>(_upstream);
                services.AddSingleton<IWebSearchClient>(_webSearch);
            });
        });
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ProxyRequiresBearerAccessApiKey()
    {
        var response = await _client.PostAsJsonAsync("/v1/responses", new { model = "m", input = "ping" });
        var payload = await ReadJson(response, HttpStatusCode.Unauthorized);

        Assert.Equal(
            "valid bearer api key required",
            payload.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("bad_request", payload.RootElement.GetProperty("error").GetProperty("type").GetString());

        var logs = OpenCodexDatabase.ReadLogs(_workspace.DatabasePath);
        Assert.Single(logs);
        Assert.Equal("failed", logs[0].RequestStatus);
        Assert.Equal(401, logs[0].StatusCode);
    }

    [Fact]
    public async Task ResponsesEndpointRoutesConvertsAndLogsNonStreamingChatUpstream()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        _upstream.Response = ChatTextResponse("pong", "gpt-5.4");
        Authorize(key);

        var response = await _client.PostAsJsonAsync("/v1/responses", new { model = "m", input = "ping" });
        var payload = await ReadJson(response);

        Assert.Equal("m", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("pong", payload.RootElement.GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString());

        Assert.Single(_upstream.Calls);
        var call = _upstream.Calls[0];
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("gpt-5.4", call.Payload["model"]);
        Assert.True(call.Payload.ContainsKey("messages"));

        var logs = OpenCodexDatabase.ReadLogs(_workspace.DatabasePath);
        var log = Assert.Single(logs);
        Assert.Equal("admin", log.OwnerUsername);
        Assert.Equal(key.Id, log.ApiKeyId);
        Assert.Equal("m", log.Model);
        Assert.Equal("gpt-5.4", log.UpstreamModel);
        Assert.Equal("chat", log.ChannelId);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(100, log.InputTokens);
        Assert.Equal(20, log.CachedTokens);
        Assert.Equal(50, log.OutputTokens);
        Assert.True(log.Cost > 0);
        Assert.Equal("success", log.RequestStatus);
        Assert.NotNull(log.RequestHeaders);
        Assert.Contains("...", log.RequestHeaders, StringComparison.Ordinal);
        Assert.Contains("ping", log.RequestBody, StringComparison.Ordinal);
        Assert.Contains("ping", log.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.Contains("pong", log.UpstreamResponseBody, StringComparison.Ordinal);
        Assert.Contains("pong", log.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessKeyOwnerSelectsOwnChannelsWithoutAdminFallback()
    {
        SetupAdminAccessKey();
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        var aliceKey = OpenCodexDatabase.CreateAccessApiKey(_workspace.DatabasePath, "alice", "Alice");
        SaveChannels(
            null,
            [
                ChatChannel("chat", "https://admin.example.test/v1", "admin-secret", "m", "admin-upstream", "admin"),
                ChatChannel("chat", "https://alice.example.test/v1", "alice-secret", "m", "alice-upstream", "alice")
            ]);
        _upstream.Response = ChatTextResponse("pong", "alice-upstream");
        Authorize(aliceKey);

        var response = await _client.PostAsJsonAsync("/v1/responses", new { model = "m", input = "alice" });
        await ReadJson(response);

        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("alice", call.Channel["owner_username"]);
        Assert.Equal("https://alice.example.test/v1", call.Channel["baseurl"]);
        Assert.Equal("alice-secret", call.Channel["apikey"]);
        Assert.Equal("alice-upstream", call.Payload["model"]);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.Equal("alice", log.OwnerUsername);
        Assert.Equal(aliceKey.Id, log.ApiKeyId);
    }

    [Fact]
    public async Task UpstreamErrorReturnsProxyErrorAndLogsFailure()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        _upstream.Exception = new UpstreamException(
            "upstream returned HTTP 502",
            StatusCodes.Status502BadGateway,
            new Dictionary<string, object?> { ["error"] = "bad gateway" },
            "chat");
        Authorize(key);

        var response = await _client.PostAsJsonAsync("/v1/responses", new { model = "m", input = "ping" });
        var payload = await ReadJson(response, HttpStatusCode.BadGateway);

        var error = payload.RootElement.GetProperty("error");
        Assert.Equal("upstream_error", error.GetProperty("type").GetString());
        Assert.Equal("chat", error.GetProperty("channel_id").GetString());

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.Equal("failed", log.RequestStatus);
        Assert.Equal(502, log.StatusCode);
        Assert.Contains("upstream returned HTTP 502", log.Error, StringComparison.Ordinal);
        Assert.Contains("bad gateway", log.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegularUserWithoutChannelsDoesNotFallBackToAdminChannels()
    {
        SetupAdminAccessKey();
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        var aliceKey = OpenCodexDatabase.CreateAccessApiKey(_workspace.DatabasePath, "alice", "Alice");
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        Authorize(aliceKey);

        var response = await _client.PostAsJsonAsync("/v1/responses", new { model = "m", input = "ping" });
        var payload = await ReadJson(response, HttpStatusCode.BadRequest);

        Assert.Contains(
            "no enabled channels configured",
            payload.RootElement.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(_upstream.Calls);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.Equal("alice", log.OwnerUsername);
        Assert.Equal("failed", log.RequestStatus);
        Assert.Equal(400, log.StatusCode);
    }

    [Fact]
    public async Task ChatEndpointStreamsSameProtocolSseAndLogsPassthrough()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        _upstream.StreamLines =
        [
            "data: {\"id\":\"chunk_1\",\"choices\":[{\"delta\":{\"content\":\"po\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chunk_1\",\"choices\":[{\"delta\":{\"content\":\"ng\"},\"finish_reason\":\"stop\"}]}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ];
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "m", messages = new[] { new { role = "user", content = "ping" } }, stream = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"po\"", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("gpt-5.4", call.Payload["model"]);
        Assert.True(call.Payload["stream"] is true);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.TtftMs);
        Assert.Contains("\"stream\":true", log.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.Equal("null", log.UpstreamResponseBody);
        Assert.Equal("null", log.ResponseBody);
    }

    [Fact]
    public async Task ResponsesEndpointStreamsChatUpstreamAndLogsReconstructedBodies()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        _upstream.StreamLines =
        [
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"po\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ng\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ];
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "m", input = "ping", stream = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", body, StringComparison.Ordinal);

        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("gpt-5.4", call.Payload["model"]);
        Assert.True(call.Payload["stream"] is true);
        Assert.True(call.Payload.ContainsKey("messages"));

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.TtftMs);
        Assert.Equal(3, log.InputTokens);
        Assert.Equal(2, log.OutputTokens);
        Assert.Contains("\"stream\":true", log.UpstreamRequestBody, StringComparison.Ordinal);
        Assert.Equal("pong", JsonDocument.Parse(log.UpstreamResponseBody!).RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString());
        Assert.Equal("pong", JsonDocument.Parse(log.ResponseBody!).RootElement
            .GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString());
    }

    [Fact]
    public async Task ResponsesEndpointStreamsChatToolCallsAndLogsReconstructedBodies()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        _upstream.StreamLines =
        [
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"exec_command\",\"arguments\":\"{\\\"cmd\\\":\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"pwd\\\"}\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ];
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "m", input = "run", stream = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: response.output_item.added", body, StringComparison.Ordinal);
        Assert.Contains("event: response.function_call_arguments.delta", body, StringComparison.Ordinal);
        Assert.Contains("event: response.function_call_arguments.done", body, StringComparison.Ordinal);
        Assert.Contains("\"call_id\":\"call_1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"exec_command\"", body, StringComparison.Ordinal);
        Assert.Contains("\"arguments\":\"{\\\"cmd\\\":\\\"pwd\\\"}\"", body, StringComparison.Ordinal);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.TtftMs);
        Assert.Equal(3, log.InputTokens);
        Assert.Equal(2, log.OutputTokens);

        var upstreamResponse = JsonDocument.Parse(log.UpstreamResponseBody!).RootElement;
        var upstreamToolCall = upstreamResponse.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("tool_calls")[0];
        Assert.Equal("call_1", upstreamToolCall.GetProperty("id").GetString());
        Assert.Equal("{\"cmd\":\"pwd\"}", upstreamToolCall.GetProperty("function").GetProperty("arguments").GetString());

        var responsePayload = JsonDocument.Parse(log.ResponseBody!).RootElement;
        var output = responsePayload.GetProperty("output");
        Assert.Equal("function_call", output[0].GetProperty("type").GetString());
        Assert.Equal("call_1", output[0].GetProperty("call_id").GetString());
        Assert.Equal("{\"cmd\":\"pwd\"}", output[0].GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task ResponsesEndpointStreamsMessagesUpstreamAndLogsReconstructedBodies()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [MessagesChannel("messages", model: "m", upstreamModel: "claude-sonnet")]);
        _upstream.StreamLines =
        [
            "event: message_start\n",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet\",\"content\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n",
            "\n",
            "event: content_block_start\n",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n",
            "\n",
            "event: content_block_delta\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"po\"}}\n",
            "\n",
            "event: content_block_delta\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ng\"}}\n",
            "\n",
            "event: message_delta\n",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n",
            "\n",
            "event: message_stop\n",
            "data: {\"type\":\"message_stop\"}\n",
            "\n"
        ];
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "m", input = "ping", stream = true });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: response.output_text.delta", body, StringComparison.Ordinal);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", body, StringComparison.Ordinal);

        var call = Assert.Single(_upstream.Calls);
        Assert.Equal("messages", call.Channel["id"]);
        Assert.Equal("claude-sonnet", call.Payload["model"]);
        Assert.True(call.Payload["stream"] is true);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.TtftMs);
        Assert.Equal(3, log.InputTokens);
        Assert.Equal(2, log.OutputTokens);
        Assert.Equal("pong", JsonDocument.Parse(log.UpstreamResponseBody!).RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString());
        Assert.Equal("pong", JsonDocument.Parse(log.ResponseBody!).RootElement
            .GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString());
    }

    [Fact]
    public async Task ResponsesEndpointRunsWebSearchAfterModelToolCallAndLogsDetails()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        EnableWebSearch([new Dictionary<string, object?> { ["key"] = "tvly-test", ["enabled"] = true }]);
        _webSearch.Result = new WebSearchProviderResult(
            true,
            200,
            12,
            null,
            null,
            new WebSearchSummary(
                "OpenAI was founded in 2015.",
                [
                    new Dictionary<string, object?>
                    {
                        ["title"] = "OpenAI",
                        ["url"] = "https://example.test/openai",
                        ["content"] = "OpenAI was founded in 2015.",
                        ["score"] = 0.9
                    }
                ],
                null),
            new Dictionary<string, object?> { ["answer"] = "OpenAI was founded in 2015." });
        _upstream.Responses.Enqueue(ChatToolResponse("call_web", "web_search", "{\"query\":\"OpenAI\"}", "gpt-5.4"));
        _upstream.Responses.Enqueue(ChatTextResponse("OpenAI was founded in 2015.", "gpt-5.4"));
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "m",
                input = "search",
                tools = new[] { new { type = "web_search" } }
            });
        var payload = await ReadJson(response);

        var output = payload.RootElement.GetProperty("output");
        Assert.Equal("web_search_call", output[0].GetProperty("type").GetString());
        Assert.Equal("OpenAI", output[0].GetProperty("action").GetProperty("query").GetString());
        Assert.Equal("message", output[1].GetProperty("type").GetString());
        var textBlock = output[1].GetProperty("content")[0];
        Assert.Contains("OpenAI was founded in 2015.", textBlock.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("来源:", textBlock.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("url_citation", textBlock.GetProperty("annotations")[0].GetProperty("type").GetString());

        var webCall = Assert.Single(_webSearch.Calls);
        Assert.Equal("tavily", webCall.Key.Provider);
        Assert.Equal("tvly-test", webCall.Key.Key);
        Assert.Equal("OpenAI", webCall.Query);
        Assert.Equal(2, _upstream.Calls.Count);
        var secondPayload = _upstream.Calls[1].Payload;
        var secondMessages = Assert.IsType<List<object?>>(secondPayload["messages"]);
        var lastMessage = Assert.IsType<Dictionary<string, object?>>(secondMessages[^1]);
        Assert.Equal("tool", lastMessage["role"]);
        Assert.Contains("OpenAI was founded", Assert.IsType<string>(lastMessage["content"]));

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.Equal(200, log.StatusCode);
        Assert.Equal("success", log.RequestStatus);
        Assert.NotNull(log.WebSearchJson);
        var webLog = JsonDocument.Parse(log.WebSearchJson!).RootElement;
        Assert.Equal("OpenAI", webLog.GetProperty("calls")[0].GetProperty("query").GetString());
        Assert.Equal(0, webLog.GetProperty("calls")[0].GetProperty("key_position").GetInt32());
        Assert.Equal(1, webLog.GetProperty("calls")[0].GetProperty("key_usage_count").GetInt32());
        Assert.Equal(1, OpenCodexDatabase.ReadWebSearchConfig(_workspace.DatabasePath).Keys[0].UsageCount);
    }

    [Fact]
    public async Task ResponsesEndpointFeedsUnavailableWebSearchResultWhenNoKeyExists()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        EnableWebSearch([]);
        _upstream.Responses.Enqueue(ChatToolResponse("call_web", "web_search", "{\"query\":\"OpenAI\"}", "gpt-5.4"));
        _upstream.Responses.Enqueue(ChatTextResponse("search was unavailable", "gpt-5.4"));
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "m",
                input = "search",
                tools = new[] { new { type = "web_search" } }
            });
        var payload = await ReadJson(response);

        Assert.Equal("web_search_call", payload.RootElement.GetProperty("output")[0].GetProperty("type").GetString());
        Assert.Empty(_webSearch.Calls);
        Assert.Equal(2, _upstream.Calls.Count);
        var secondMessages = Assert.IsType<List<object?>>(_upstream.Calls[1].Payload["messages"]);
        var toolMessage = Assert.IsType<Dictionary<string, object?>>(secondMessages[^1]);
        var toolResult = JsonDocument.Parse(Assert.IsType<string>(toolMessage["content"])).RootElement;
        Assert.Equal("搜索不可用", toolResult.GetProperty("error").GetString());

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        var webLog = JsonDocument.Parse(log.WebSearchJson!).RootElement;
        Assert.Equal("failed", webLog.GetProperty("calls")[0].GetProperty("status").GetString());
        Assert.Equal("搜索不可用", webLog.GetProperty("calls")[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task RegularUserWebSearchDeclarationDoesNotRunLocalSimulation()
    {
        SetupAdminAccessKey();
        OpenCodexDatabase.CreateUser(_workspace.DatabasePath, "alice", "alice-pw");
        var aliceKey = OpenCodexDatabase.CreateAccessApiKey(_workspace.DatabasePath, "alice", "Alice");
        SaveChannels("alice", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4", owner: "alice")]);
        EnableWebSearch([new Dictionary<string, object?> { ["key"] = "tvly-test", ["enabled"] = true }]);
        _upstream.Response = ChatToolResponse("call_web", "web_search", "{\"query\":\"OpenAI\"}", "gpt-5.4");
        Authorize(aliceKey);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "m",
                input = "search",
                tools = new[] { new { type = "web_search" } }
            });
        var payload = await ReadJson(response);

        Assert.Equal("function_call", payload.RootElement.GetProperty("output")[0].GetProperty("type").GetString());
        Assert.Empty(_webSearch.Calls);
        Assert.Single(_upstream.Calls);
        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.Null(log.WebSearchJson);
    }

    [Fact]
    public async Task ResponsesEndpointStreamsWebSearchCallAndContinuesWithFinalAnswer()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        EnableWebSearch([new Dictionary<string, object?> { ["key"] = "tvly-test", ["enabled"] = true }]);
        _webSearch.Result = new WebSearchProviderResult(
            true,
            200,
            12,
            null,
            null,
            new WebSearchSummary(
                "answer",
                [
                    new Dictionary<string, object?>
                    {
                        ["title"] = "OpenAI",
                        ["url"] = "https://example.test/openai",
                        ["content"] = "answer",
                        ["score"] = 0.9
                    }
                ],
                null),
            new Dictionary<string, object?> { ["answer"] = "answer" });
        _upstream.StreamLineBatches.Enqueue(
        [
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_web\",\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\\\"Open\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"AI\\\"}\"}}]},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ]);
        _upstream.StreamLineBatches.Enqueue(
        [
            "data: {\"id\":\"chatcmpl_answer\",\"object\":\"chat.completion.chunk\",\"created\":2,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"answer\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_answer\",\"object\":\"chat.completion.chunk\",\"created\":2,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ]);
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "m",
                input = "search",
                tools = new[] { new { type = "web_search" } },
                stream = true
            });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: response.output_item.added", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"web_search_call\"", body, StringComparison.Ordinal);
        Assert.Contains("answer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"web_search\"", body, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(body, "event: response.created"));

        var webCall = Assert.Single(_webSearch.Calls);
        Assert.Equal("OpenAI", webCall.Query);
        Assert.Equal("tavily", webCall.Key.Provider);
        Assert.Equal("tvly-test", webCall.Key.Key);
        Assert.Equal(2, _upstream.Calls.Count);
        Assert.True(_upstream.Calls[1].Payload["stream"] is true);
        var secondMessages = Assert.IsType<List<object?>>(_upstream.Calls[1].Payload["messages"]);
        var toolMessage = Assert.IsType<Dictionary<string, object?>>(secondMessages[^1]);
        Assert.Equal("tool", toolMessage["role"]);
        Assert.Equal("call_web", toolMessage["tool_call_id"]);
        Assert.Contains("answer", Assert.IsType<string>(toolMessage["content"]), StringComparison.Ordinal);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.NotNull(log.TtftMs);
        Assert.Equal(4, log.InputTokens);
        Assert.Equal(2, log.OutputTokens);
        Assert.NotNull(log.WebSearchJson);
        Assert.Contains("web_search_call", log.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("answer", log.UpstreamResponseBody, StringComparison.Ordinal);
        var webLog = JsonDocument.Parse(log.WebSearchJson!).RootElement;
        Assert.Equal("OpenAI", webLog.GetProperty("calls")[0].GetProperty("query").GetString());
    }

    [Fact]
    public async Task ResponsesEndpointStreamsWebSearchDeclaredButNoToolCallUsesNormalStream()
    {
        var key = SetupAdminAccessKey();
        SaveChannels("admin", [ChatChannel("chat", model: "m", upstreamModel: "gpt-5.4")]);
        EnableWebSearch([new Dictionary<string, object?> { ["key"] = "tvly-test", ["enabled"] = true }]);
        _upstream.StreamLines =
        [
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"po\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ng\"},\"finish_reason\":null}]}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.4\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ];
        Authorize(key);

        var response = await _client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "m",
                input = "hello",
                tools = new[] { new { type = "web_search" } },
                stream = true
            });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("pong", body, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"web_search_call\"", body, StringComparison.Ordinal);
        Assert.Empty(_webSearch.Calls);
        Assert.Single(_upstream.Calls);

        var log = Assert.Single(OpenCodexDatabase.ReadLogs(_workspace.DatabasePath));
        Assert.True(log.IsStream);
        Assert.Equal(200, log.StatusCode);
        Assert.Null(log.WebSearchJson);
        Assert.Equal(3, log.InputTokens);
        Assert.Equal(2, log.OutputTokens);
        Assert.Contains("pong", log.ResponseBody, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _client.Dispose();
        _workspace.Dispose();
    }

    private AccessApiKeyRecord SetupAdminAccessKey()
    {
        OpenCodexDatabase.EnsureSuperadmin(_workspace.DatabasePath, "admin", "pw");
        return OpenCodexDatabase.CreateAccessApiKey(_workspace.DatabasePath, "admin", "Admin");
    }

    private void Authorize(AccessApiKeyRecord key)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
    }

    private void SaveChannels(string? ownerUsername, IReadOnlyList<IReadOnlyDictionary<string, object?>> channels)
    {
        OpenCodexDatabase.ReplaceChannels(
            _workspace.DatabasePath,
            channels,
            defaultTimeout: 120,
            ownerUsername: ownerUsername,
            defaultOwnerUsername: "admin");
    }

    private void EnableWebSearch(IReadOnlyList<Dictionary<string, object?>> keys)
    {
        OpenCodexDatabase.ReplaceWebSearchConfig(
            _workspace.DatabasePath,
            new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["keys"] = keys.Select(key => (object?)key).ToList()
            });
    }

    private static Dictionary<string, object?> ChatChannel(
        string id,
        string baseUrl = "https://example.test/v1",
        string apiKey = "upstream-secret",
        string model = "m",
        string upstreamModel = "gpt-5.4",
        string owner = "admin")
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = owner,
            ["id"] = id,
            ["type"] = "chat",
            ["baseurl"] = baseUrl,
            ["apikey"] = apiKey,
            ["auth_mode"] = "config",
            ["models"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["upstream_model"] = upstreamModel
                }
            }
        };
    }

    private static Dictionary<string, object?> MessagesChannel(
        string id,
        string baseUrl = "https://example.test/v1",
        string apiKey = "upstream-secret",
        string model = "m",
        string upstreamModel = "claude-sonnet",
        string owner = "admin")
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = owner,
            ["id"] = id,
            ["type"] = "messages",
            ["baseurl"] = baseUrl,
            ["apikey"] = apiKey,
            ["auth_mode"] = "config",
            ["models"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["upstream_model"] = upstreamModel
                }
            }
        };
    }

    private static Dictionary<string, object?> ChatTextResponse(string text, string model)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 100,
                ["prompt_tokens_details"] = new Dictionary<string, object?>
                {
                    ["cached_tokens"] = 20
                },
                ["completion_tokens"] = 50
            }
        };
    }

    private static Dictionary<string, object?> ChatToolResponse(
        string callId,
        string toolName,
        string arguments,
        string model)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = callId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = toolName,
                                    ["arguments"] = arguments
                                }
                            }
                        }
                    },
                    ["finish_reason"] = "tool_calls"
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 3,
                ["completion_tokens"] = 2,
                ["total_tokens"] = 5
            }
        };
    }

    private static async Task<JsonDocument> ReadJson(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public List<UpstreamCall> Calls { get; } = [];

        public Dictionary<string, object?> Response { get; set; } = [];

        public Queue<Dictionary<string, object?>> Responses { get; } = [];

        public IReadOnlyList<string> StreamLines { get; set; } = [];

        public Queue<IReadOnlyList<string>> StreamLineBatches { get; } = [];

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

            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Response);
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

            var streamLines = StreamLineBatches.Count > 0
                ? StreamLineBatches.Dequeue()
                : StreamLines;
            foreach (var line in streamLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return line;
            }
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
            new WebSearchSummary("answer", [], null),
            new Dictionary<string, object?>());

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
