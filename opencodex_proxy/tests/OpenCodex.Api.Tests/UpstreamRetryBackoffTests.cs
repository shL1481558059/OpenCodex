using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using Xunit;

namespace OpenCodex.Api.Tests;

// 覆盖"重试前必须等待"的行为：退避下限、指数序列、Retry-After 优先与截断。
public sealed class UpstreamRetryBackoffTests
{
    // 抖动只向上浮动 20%，断言用区间而不是精确值。
    private const double JitterCeilingRatio = 1.2;

    private static readonly Dictionary<string, object?> Payload = new()
    {
        ["model"] = "gpt-test",
        ["messages"] = new List<object?>()
    };

    // 按队列依次执行预设步骤，每步返回一个响应或抛出网络异常。
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;

        public int CallCount { get; private set; }

        public ScriptedHandler(params Func<HttpResponseMessage>[] steps)
        {
            _steps = new Queue<Func<HttpResponseMessage>>(steps);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_steps.Dequeue()());
        }
    }

    private static Dictionary<string, object?> ChatChannel(int retryCount)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "test-channel",
            ["type"] = "chat",
            ["baseurl"] = "https://upstream.test/v1",
            ["auth_mode"] = "none",
            ["retry_count"] = retryCount
        };
    }

    private static (HttpUpstreamClient Client, List<TimeSpan> Delays) CreateClient(ScriptedHandler handler)
    {
        var delays = new List<TimeSpan>();
        var client = new HttpUpstreamClient(
            new HttpClient(handler),
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });
        return (client, delays);
    }

    private static Func<HttpResponseMessage> Failure(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        return () =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"error":{"message":"nope"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
            if (retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        };
    }

    private static Func<HttpResponseMessage> JsonOk()
    {
        return () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"ok"}""", Encoding.UTF8, "application/json")
        };
    }

    private static Func<HttpResponseMessage> Sse(string dataLine, TimeSpan? retryAfter = null)
    {
        return () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{dataLine}\n\n", Encoding.UTF8, "text/event-stream")
            };
            if (retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        };
    }

    private static Func<HttpResponseMessage> NetworkError()
    {
        return () => throw new HttpRequestException("connection refused");
    }

    private static void AssertDelayAtLeast(TimeSpan expectedBase, TimeSpan actual)
    {
        Assert.True(
            actual >= expectedBase,
            $"期望至少等待 {expectedBase.TotalSeconds}s，实际 {actual.TotalSeconds}s");
        Assert.True(
            actual <= expectedBase * JitterCeilingRatio,
            $"期望不超过 {(expectedBase * JitterCeilingRatio).TotalSeconds}s，实际 {actual.TotalSeconds}s");
    }

    [Fact]
    public async Task PostJsonAsync_RetryableStatuses_WaitsExponentialSecondsBetweenAttempts()
    {
        var handler = new ScriptedHandler(
            Failure(HttpStatusCode.TooManyRequests),
            Failure(HttpStatusCode.ServiceUnavailable),
            Failure(HttpStatusCode.BadGateway),
            JsonOk());
        var (client, delays) = CreateClient(handler);

        var result = await client.PostJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None);

        Assert.Equal("ok", result["id"]);
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(3, delays.Count);
        AssertDelayAtLeast(TimeSpan.FromSeconds(2), delays[0]);
        AssertDelayAtLeast(TimeSpan.FromSeconds(4), delays[1]);
        AssertDelayAtLeast(TimeSpan.FromSeconds(8), delays[2]);
    }

    [Fact]
    public async Task PostJsonAsync_RetryAfterZero_StillWaitsBaseDelay()
    {
        var handler = new ScriptedHandler(
            Failure(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            JsonOk());
        var (client, delays) = CreateClient(handler);

        await client.PostJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None);

        var delay = Assert.Single(delays);
        AssertDelayAtLeast(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public async Task PostJsonAsync_RetryAfterHeader_IsHonoredAndCappedAt30Seconds()
    {
        var handler = new ScriptedHandler(
            Failure(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(12)),
            Failure(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(120)),
            JsonOk());
        var (client, delays) = CreateClient(handler);

        await client.PostJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None);

        Assert.Equal(2, delays.Count);
        AssertDelayAtLeast(TimeSpan.FromSeconds(12), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), delays[1]);
    }

    [Fact]
    public async Task PostJsonAsync_NoRetryBudget_DoesNotWait()
    {
        var handler = new ScriptedHandler(Failure(HttpStatusCode.TooManyRequests));
        var (client, delays) = CreateClient(handler);

        await Assert.ThrowsAsync<UpstreamException>(
            () => client.PostJsonAsync(ChatChannel(0), Payload, 30, CancellationToken.None));

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task StreamJsonAsync_NetworkError_WaitsBeforeRetrying()
    {
        var handler = new ScriptedHandler(
            NetworkError(),
            NetworkError(),
            Sse("""data: {"id":"chatcmpl-1","choices":[{"delta":{"content":"hi"}}]}"""));
        var (client, delays) = CreateClient(handler);

        var lines = new List<string>();
        await foreach (var line in client.StreamJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(3, handler.CallCount);
        Assert.NotEmpty(lines);
        Assert.Equal(2, delays.Count);
        AssertDelayAtLeast(TimeSpan.FromSeconds(2), delays[0]);
        AssertDelayAtLeast(TimeSpan.FromSeconds(4), delays[1]);
    }

    [Fact]
    public async Task StreamJsonAsync_RetryableSseError_HonorsRetryAfterHeader()
    {
        var handler = new ScriptedHandler(
            Sse(
                """data: {"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""",
                TimeSpan.FromSeconds(10)),
            Sse("""data: {"id":"chatcmpl-1","choices":[{"delta":{"content":"hi"}}]}"""));
        var (client, delays) = CreateClient(handler);

        var lines = new List<string>();
        await foreach (var line in client.StreamJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(2, handler.CallCount);
        Assert.NotEmpty(lines);
        var delay = Assert.Single(delays);
        AssertDelayAtLeast(TimeSpan.FromSeconds(10), delay);
    }

    [Fact]
    public async Task StreamJsonAsync_RetryableStatus_WaitsBeforeRetrying()
    {
        var handler = new ScriptedHandler(
            Failure(HttpStatusCode.ServiceUnavailable),
            Sse("""data: {"id":"chatcmpl-1","choices":[{"delta":{"content":"hi"}}]}"""));
        var (client, delays) = CreateClient(handler);

        var lines = new List<string>();
        await foreach (var line in client.StreamJsonAsync(ChatChannel(3), Payload, 30, CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(2, handler.CallCount);
        Assert.NotEmpty(lines);
        var delay = Assert.Single(delays);
        AssertDelayAtLeast(TimeSpan.FromSeconds(2), delay);
    }
}
