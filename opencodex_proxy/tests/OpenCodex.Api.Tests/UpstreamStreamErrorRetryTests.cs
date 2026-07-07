using System.Net;
using System.Text;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class UpstreamStreamErrorRetryTests
{
    // 模拟上游返回 HTTP 200 + SSE body，body 内容由 lines 拼接。
    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly Queue<string[]> _responses;
        public int CallCount { get; private set; }

        public SseHandler(params string[][] responses)
        {
            _responses = new Queue<string[]>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var lines = _responses.Dequeue();
            var body = string.Join("\n", lines) + "\n";
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private static Dictionary<string, object?> ChatChannel(int retryCount) => new()
    {
        ["id"] = "test-channel",
        ["type"] = "chat",
        ["baseurl"] = "https://upstream.test/v1",
        ["auth_mode"] = "none",
        ["retry_count"] = retryCount
    };

    [Fact]
    public async Task StreamJsonAsync_RateLimitError_RetriesAndSucceedsOnSecondAttempt()
    {
        var handler = new SseHandler(
            // 第一次：上游返回并发超限 error
            [
                """data: {"type":"error","error":{"type":"rate_limit_error","message":"Concurrency limit exceeded for account, please retry later"}}""",
                ""
            ],
            // 第二次：正常流
            [
                """data: {"id":"chatcmpl-1","object":"chat.completion.chunk","choices":[{"delta":{"role":"assistant","content":"hi"}}]}""",
                "",
                """data: {"id":"chatcmpl-1","object":"chat.completion.chunk","choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "",
                "data: [DONE]",
                ""
            ]
        );
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var lines = new List<string>();
        await foreach (var line in upstream.StreamJsonAsync(
            ChatChannel(retryCount: 2),
            new Dictionary<string, object?> { ["model"] = "test" },
            30,
            CancellationToken.None))
        {
            lines.Add(line.TrimEnd('\n'));
        }

        Assert.Equal(2, handler.CallCount);
        // 客户端不应看到 rate_limit_error
        Assert.DoesNotContain(lines, l => l.Contains("rate_limit_error"));
        Assert.Contains(lines, l => l.Contains("chatcmpl-1"));
    }

    [Fact]
    public async Task StreamJsonAsync_RateLimitError_RetriesExhausted_ThrowsUpstreamException()
    {
        var handler = new SseHandler(
            [
                """data: {"type":"error","error":{"type":"rate_limit_error","message":"Concurrency limit exceeded"}}""",
                ""
            ],
            [
                """data: {"type":"error","error":{"type":"rate_limit_error","message":"Concurrency limit exceeded"}}""",
                ""
            ]
        );
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await foreach (var _ in upstream.StreamJsonAsync(
                ChatChannel(retryCount: 1),
                new Dictionary<string, object?> { ["model"] = "test" },
                30,
                CancellationToken.None))
            {
            }
        });

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(ProxyHttpStatus.TooManyRequests, ex.StatusCode);
        Assert.Contains("Concurrency limit", ex.Message);
    }

    [Fact]
    public async Task StreamJsonAsync_NormalStream_NotAffectedByProbe()
    {
        var handler = new SseHandler(
            [
                """data: {"id":"chatcmpl-1","object":"chat.completion.chunk","choices":[{"delta":{"role":"assistant","content":"hello"}}]}""",
                "",
                """data: {"id":"chatcmpl-1","object":"chat.completion.chunk","choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "",
                "data: [DONE]",
                ""
            ]
        );
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var lines = new List<string>();
        await foreach (var line in upstream.StreamJsonAsync(
            ChatChannel(retryCount: 2),
            new Dictionary<string, object?> { ["model"] = "test" },
            30,
            CancellationToken.None))
        {
            lines.Add(line.TrimEnd('\n'));
        }

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(lines, l => l.Contains("hello"));
        Assert.Contains(lines, l => l == "data: [DONE]");
    }

    [Fact]
    public async Task StreamJsonAsync_OverloadedError_Retries()
    {
        var handler = new SseHandler(
            [
                """data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""",
                ""
            ],
            [
                """data: {"id":"chatcmpl-2","object":"chat.completion.chunk","choices":[{"delta":{"content":"ok"}}]}""",
                "",
                "data: [DONE]",
                ""
            ]
        );
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var lines = new List<string>();
        await foreach (var line in upstream.StreamJsonAsync(
            ChatChannel(retryCount: 2),
            new Dictionary<string, object?> { ["model"] = "test" },
            30,
            CancellationToken.None))
        {
            lines.Add(line.TrimEnd('\n'));
        }

        Assert.Equal(2, handler.CallCount);
        Assert.DoesNotContain(lines, l => l.Contains("overloaded_error"));
        Assert.Contains(lines, l => l.Contains("chatcmpl-2"));
    }

    [Fact]
    public async Task StreamJsonAsync_NonRetryableError_NotRetried_TransparentToClient()
    {
        // invalid_request_error 不在可重试列表中，应原样透传给客户端
        var handler = new SseHandler(
            [
                """data: {"type":"error","error":{"type":"invalid_request_error","message":"bad request"}}""",
                ""
            ]
        );
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var lines = new List<string>();
        await foreach (var line in upstream.StreamJsonAsync(
            ChatChannel(retryCount: 2),
            new Dictionary<string, object?> { ["model"] = "test" },
            30,
            CancellationToken.None))
        {
            lines.Add(line.TrimEnd('\n'));
        }

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(lines, l => l.Contains("invalid_request_error"));
    }

    [Fact]
    public async Task PostJsonAsync_RateLimitErrorInBody_ThrowsTooManyRequests()
    {
        var handler = new StaticJsonHandler(
            """{"type":"error","error":{"type":"rate_limit_error","message":"Concurrency limit exceeded"}}""");
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<UpstreamException>(async () =>
        {
            await upstream.PostJsonAsync(
                ChatChannel(retryCount: 0),
                new Dictionary<string, object?> { ["model"] = "test" },
                30,
                CancellationToken.None);
        });

        Assert.Equal(ProxyHttpStatus.TooManyRequests, ex.StatusCode);
        Assert.Contains("Concurrency limit", ex.Message);
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticJsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
