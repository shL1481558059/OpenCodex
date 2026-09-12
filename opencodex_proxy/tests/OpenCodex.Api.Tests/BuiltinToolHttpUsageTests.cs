using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class BuiltinToolHttpUsageTests
{
    [Theory]
    [InlineData("chat", false, 30, 0, 9)]
    [InlineData("chat", true, 30, 0, 9)]
    [InlineData("messages", false, 48, 8, 10)]
    [InlineData("messages", true, 48, 8, 10)]
    public async Task HttpDecodedUsage_IsBilledAcrossToolRounds(
        string protocol, bool stream, int inputTokens, int cacheWrite, int cacheRead)
    {
        using var transport = new RecordedHttpHandler(protocol, stream);
        using var upstreamHttp = new HttpClient(transport);
        var upstream = new HttpUpstreamClient(upstreamHttp);
        var search = new SearchExecutor();
        using var root = new OpenCodexApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUpstreamClient>();
            services.AddSingleton<IUpstreamClient>(upstream);
            services.RemoveAll<IWebSearchToolExecutor>();
            services.AddSingleton<IWebSearchToolExecutor>(search);
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false, AllowAutoRedirect = false
        });
        using var login = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = OpenCodexApiFactory.AdminPassword
        }));
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        await AdminAsync(client, cookie, HttpMethod.Post, "/channels", new
        {
            name = "http-usage-replay", type = protocol, baseurl = "https://offline.invalid/v1",
            apikey = "unused", auth_mode = "config", enabled = true, retry_count = 0,
            timeout_seconds = 30, priority = 0, capacity = 1, circuit_break_duration_seconds = 0,
            models = new[] { new { model = "public", upstream_model = "upstream" } }
        });
        var key = await AdminAsync(client, cookie, HttpMethod.Post, "/api-keys", new { name = "usage-replay" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "public", input = "Search once.", stream, max_tool_calls = 1,
                tools = new[] { new { type = "web_search" } }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.GetProperty("key").GetProperty("key").GetString());
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("answer", body, StringComparison.Ordinal);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, search.Calls);

        var logs = await AdminAsync(client, cookie, HttpMethod.Get, "/logs?request_type=main", null);
        var log = Assert.Single(logs.GetProperty("events").EnumerateArray());
        Assert.Equal(inputTokens, log.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, log.GetProperty("output_tokens").GetInt32());
        Assert.Equal(cacheWrite + cacheRead, log.GetProperty("cached_tokens").GetInt32());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IOpenCodexDbContext>();
        var logId = log.GetProperty("id").GetGuid();
        var stored = db.RequestLogs.Single(item => item.Id == logId);
        Assert.Equal(cacheWrite, stored.CacheWriteTokens);
        Assert.Equal(cacheRead, stored.CacheReadTokens);
        if (protocol == "chat" && stream)
        {
            var completed = new List<JsonElement>();
            await foreach (var item in SseStreamConverter.ParseEvents(Lines(body), CancellationToken.None))
            {
                if (item.Data is not Dictionary<string, object?> data
                    || !data.TryGetValue("type", out var type) || type is not "response.completed")
                    continue;
                completed.Add(JsonSerializer.SerializeToElement(data["response"]));
            }
            var usage = Assert.Single(completed).GetProperty("usage");
            Assert.Equal(3, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
            Assert.Equal(cacheRead, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
            var detail = await AdminAsync(client, cookie, HttpMethod.Get, "/logs/" + logId, null);
            using var loggedResponse = JsonDocument.Parse(detail.GetProperty("response_body").GetString()!);
            Assert.Equal(3, loggedResponse.RootElement.GetProperty("usage").GetProperty("output_tokens_details")
                .GetProperty("reasoning_tokens").GetInt32());
        }
    }

    private static async IAsyncEnumerable<string> Lines(string body)
    {
        using var reader = new StringReader(body);
        while (await reader.ReadLineAsync() is { } line) yield return line;
    }

    private static async Task<JsonElement> AdminAsync(HttpClient client, string cookie, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {text}");
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        return document.RootElement.GetProperty("Data").Clone();
    }

    private sealed class SearchExecutor : IWebSearchToolExecutor
    {
        public int Calls { get; private set; }
        public string CurrentMode() => WebSearchModes.Simulate;
        public Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken cancellationToken)
        {
            Calls++;
            var result = new Dictionary<string, object?> { ["answer"] = "source", ["results"] = new List<object?>() };
            return Task.FromResult(new WebSearchToolResult(
                callId, "query", "completed", JsonSerializer.Serialize(result), result,
                null, "fake", null, null, null, null, null, 200, null));
        }
    }

    private sealed class RecordedHttpHandler(string protocol, bool stream) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.InRange(Calls, 1, 2);
            var first = Calls == 1;
            var usage = protocol == "chat"
                ? new Dictionary<string, object?>
                {
                    ["prompt_tokens"] = first ? 10 : 20, ["completion_tokens"] = first ? 2 : 3,
                    ["prompt_tokens_details"] = new Dictionary<string, object?> { ["cached_tokens"] = first ? 4 : 5 },
                    ["completion_tokens_details"] = new Dictionary<string, object?> { ["reasoning_tokens"] = first ? 1 : 2 }
                }
                : new Dictionary<string, object?>
                {
                    ["input_tokens"] = first ? 10 : 20, ["output_tokens"] = first ? 2 : 3,
                    ["cache_creation_input_tokens"] = first ? 3 : 5, ["cache_read_input_tokens"] = first ? 4 : 6
                };
            var tool = new Dictionary<string, object?>
            {
                ["id"] = "search_1", ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = "opencodex_web_search", ["arguments"] = "{\"query\":\"query\"}"
                }
            };
            var message = new Dictionary<string, object?>
            {
                ["role"] = "assistant", ["content"] = first ? null : "answer"
            };
            if (first) message["tool_calls"] = new List<object?> { tool };
            var content = new List<object?>
            {
                first
                    ? new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use", ["id"] = "search_1", ["name"] = "opencodex_web_search",
                        ["input"] = new Dictionary<string, object?> { ["query"] = "query" }
                    }
                    : new Dictionary<string, object?> { ["type"] = "text", ["text"] = "answer" }
            };
            string body;
            if (!stream)
            {
                body = protocol == "chat"
                    ? JsonSerializer.Serialize(new { id = "chat", model = "upstream", usage, choices = new[] { new { index = 0, message, finish_reason = first ? "tool_calls" : "stop" } } })
                    : JsonSerializer.Serialize(new { id = "message", type = "message", role = "assistant", model = "upstream", content, usage, stop_reason = first ? "tool_use" : "end_turn" });
            }
            else if (protocol == "chat")
            {
                tool["index"] = 0;
                body = Event(null, new { id = "chat", model = "upstream", choices = new[] { new { index = 0, delta = message } } })
                    + Event(null, new { id = "chat", model = "upstream", usage, choices = new[] { new { index = 0, delta = new { }, finish_reason = first ? "tool_calls" : "stop" } } })
                    + "data: [DONE]\n\n";
            }
            else
            {
                body = Event("message_start", new { type = "message_start", message = new { id = "message", type = "message", role = "assistant", model = "upstream", usage } })
                    + Event("content_block_start", new { type = "content_block_start", index = 0, content_block = content[0] })
                    + Event("content_block_stop", new { type = "content_block_stop", index = 0 })
                    + Event("message_delta", new { type = "message_delta", delta = new { stop_reason = first ? "tool_use" : "end_turn" }, usage = new { output_tokens = first ? 2 : 3 } })
                    + Event("message_stop", new { type = "message_stop" });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            });
        }

        private static string Event(string? name, object payload) =>
            (name is null ? "" : $"event: {name}\n") + $"data: {JsonSerializer.Serialize(payload)}\n\n";
    }
}
