using System.Net;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ImagesUpstreamClientTests
{
    [Fact]
    public async Task Generate_SendsCompleteMappedJsonOnceAndOwnsResponse()
    {
        var responseStream = new TrackingStream(Encoding.UTF8.GetBytes("{\"data\":[]}"));
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream),
            Headers = { { "x-request-id", "request-1" }, { "set-cookie", "secret" } }
        });
        var client = new HttpUpstreamClient(new HttpClient(handler));
        var parameters = Parameters().WithModel("upstream-image");

        await using (var response = await client.GenerateAsync(Channel("openai", retryCount: 9),
            new ImageGenerationRequest(parameters), 30, default))
        {
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("request-1", response.Headers["x-request-id"]);
            Assert.DoesNotContain("set-cookie", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(1, handler.SendCount);
        Assert.Equal("https://example.test/v1/images/generations", handler.Uri);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("upstream-image", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("16:9", json.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.True(responseStream.DisposeCount > 0);
    }

    [Fact]
    public async Task EditOpenAi_RebuildsOrderedMultipartWithoutLosingScalars()
    {
        var opens = 0;
        var handler = new CaptureHandler(_ => JsonResponse());
        var client = new HttpUpstreamClient(new HttpClient(handler));
        var request = new ImageEditRequest(Parameters(),
        [
            CountingFile("image[]", "first.png", "image/png", [1, 2], () => opens++),
            CountingFile("image[]", "second.webp", "image/webp", [3, 4, 5], () => opens++)
        ], File("mask", "mask.png", "image/png", [9]));

        await using var response = await client.EditAsync(Channel("openai"), request, 30, default);

        Assert.Equal(1, handler.SendCount);
        Assert.Contains("first.png", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("second.webp", handler.Body!, StringComparison.Ordinal);
        Assert.True(handler.Body!.IndexOf("first.png", StringComparison.Ordinal) < handler.Body.IndexOf("second.webp", StringComparison.Ordinal));
        Assert.Contains("mask.png", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("aspect_ratio", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("16:9", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("\u0001\u0002", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("\u0003\u0004\u0005", handler.Body!, StringComparison.Ordinal);
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task EditXai_UsesDataUriShapesAndRejectsMaskBeforeNetwork()
    {
        var handler = new CaptureHandler(_ => JsonResponse());
        var client = new HttpUpstreamClient(new HttpClient(handler));
        await using var response = await client.EditAsync(Channel("xai"), new ImageEditRequest(
            Parameters(),
            [File("image", "one.png", "image/png", [1]), File("image", "two.jpg", "image/jpeg", [2])]), 30, default);

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, json.RootElement.GetProperty("images").GetArrayLength());
        Assert.StartsWith("data:image/png;base64,", json.RootElement.GetProperty("images")[0].GetProperty("url").GetString());
        Assert.False(json.RootElement.TryGetProperty("size", out _));

        await Assert.ThrowsAsync<BadRequestException>(async () => await client.EditAsync(Channel("xai"), new ImageEditRequest(
            Parameters(), [File("image", "one.png", "image/png", [1])], File("mask", "mask.png", "image/png", [9])), 30, default));
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task EditXai_ValidatesShapeAndUnsupportedFieldsBeforeNetwork()
    {
        var handler = new CaptureHandler(_ => JsonResponse());
        var client = new HttpUpstreamClient(new HttpClient(handler));
        var singlePayload = new Dictionary<string, object?>
        {
            ["model"] = "mapped", ["prompt"] = "draw", ["storage_options"] = new Dictionary<string, object?> { ["bucket"] = "b" },
            ["response_format"] = "b64_json", ["user"] = "u"
        };
        await using (var response = await client.EditAsync(Channel("xai"), new ImageEditRequest(
            new ImageProxyParameters(singlePayload), [File("image", "one.png", "image/png", [1])]), 30, default))
        {
            using var json = JsonDocument.Parse(handler.Body!);
            Assert.True(json.RootElement.TryGetProperty("image", out _));
            Assert.False(json.RootElement.TryGetProperty("images", out _));
            Assert.Equal("b64_json", json.RootElement.GetProperty("response_format").GetString());
            Assert.Equal("b", json.RootElement.GetProperty("storage_options").GetProperty("bucket").GetString());
        }

        var unsupported = new ImageProxyParameters(new Dictionary<string, object?> { ["prompt"] = "draw", ["size"] = "1024x1024" });
        await Assert.ThrowsAsync<BadRequestException>(async () => await client.EditAsync(Channel("xai"),
            new ImageEditRequest(unsupported, [File("image", "one.png", "image/png", [1])]), 30, default));
        await Assert.ThrowsAsync<BadRequestException>(async () => await client.EditAsync(Channel("xai"),
            new ImageEditRequest(new ImageProxyParameters(singlePayload),
            [File("image", "1.png", "image/png", [1]), File("image", "2.png", "image/png", [2]), File("image", "3.png", "image/png", [3]), File("image", "4.png", "image/png", [4])]), 30, default));
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task Failure_IsBoundedAndNeverRetried()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(new string('x', 100_000))
        });
        var client = new HttpUpstreamClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<UpstreamException>(async () =>
            await client.GenerateAsync(Channel("openai", retryCount: 10), new ImageGenerationRequest(Parameters()), 30, default));

        Assert.Equal(1, handler.SendCount);
        Assert.True(exception.Body?.ToString()?.Length <= 65_536);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedWithoutRetry()
    {
        var handler = new CancelHandler();
        var client = new HttpUpstreamClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GenerateAsync(Channel("openai", retryCount: 10), new ImageGenerationRequest(Parameters()), 30, cts.Token));
        Assert.Equal(1, handler.SendCount);
    }

    private static ImageProxyParameters Parameters() => new(new Dictionary<string, object?>
    {
        ["model"] = "public-image",
        ["prompt"] = "draw",
        ["aspect_ratio"] = "16:9"
    });

    private static Dictionary<string, object?> Channel(string dialect, int retryCount = 0) => new()
    {
        ["id"] = "images",
        ["type"] = "images",
        ["baseurl"] = "https://example.test/v1",
        ["apikey"] = "secret",
        ["auth_mode"] = "config",
        ["timeout_seconds"] = 30,
        ["retry_count"] = retryCount,
        ["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = dialect }
    };

    private static ImageProxyFileSource File(string field, string name, string mime, byte[] bytes)
        => new(field, name, mime, bytes.Length, _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes)));

    private static ImageProxyFileSource CountingFile(string field, string name, string mime, byte[] bytes, Action opened)
        => new(field, name, mime, bytes.Length, _ => { opened(); return ValueTask.FromResult<Stream>(new MemoryStream(bytes)); });

    private static HttpResponseMessage JsonResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
    };

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            Uri = request.RequestUri?.ToString();
            Body = request.Content is null ? null : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return response(request);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }

    private sealed class CancelHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}
