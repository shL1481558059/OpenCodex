using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.Core.Protocols;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class SsePushResponseWriterTests
{
    [Fact]
    public async Task CompleteEvents_AreBatchedWithoutDoneOrMutatingQueuedSnapshots()
    {
        var context = new DefaultHttpContext();
        var body = new CountingStream(); context.Response.Body = body;
        await using var writer = new SsePushResponseWriter(context.Response, default, new() { MaxFlushDelayMilliseconds = 60_000 });
        await writer.EmitAsync(Event("response.created"));
        var item = Event("response.output_text.delta"); item["delta"] = "before";
        for (var i = 0; i < 100; i++) await writer.EmitAsync(item);
        item["delta"] = "after";
        await writer.EmitAsync(Event("response.completed"));
        await writer.CompleteAsync();
        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(100, text.Split("\"delta\":\"before\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("after", text);
        Assert.DoesNotContain("[DONE]", text);
        Assert.True(body.Flushes < 10, $"flushes={body.Flushes}");
        Assert.Equal(0, writer.QueuedBytes);
    }

    [Fact]
    public async Task TerminalFlushFailure_IsObservedAndCannotPublishSecondTerminal()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new TerminalFailureStream();
        await using var writer = new SsePushResponseWriter(context.Response, default);
        await writer.EmitAsync(Event("response.created"));
        await Assert.ThrowsAsync<IOException>(() => writer.EmitAsync(Event("response.completed")));
        Assert.True(writer.TerminalCommitted);
        await Assert.ThrowsAsync<IOException>(() => writer.EmitAsync(Event("response.failed")));
        await Assert.ThrowsAsync<IOException>(writer.CompleteAsync);
        Assert.Equal(0, writer.QueuedBytes);
    }

    [Fact]
    public async Task SlowDownstream_BackpressureIsByteBoundedAndCancellationReleasesQueue()
    {
        using var stop = new CancellationTokenSource();
        var body = new BlockedFlushStream();
        var context = new DefaultHttpContext(); context.Response.Body = body;
        await using var writer = new SsePushResponseWriter(context.Response, stop.Token,
            new() { MaxQueuedBytes = 256, MaxQueuedEvents = 2 });
        await writer.EmitAsync(Event("response.created"));
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var produce = Produce();
        Assert.False(produce.IsCompleted);
        Assert.InRange(writer.QueuedBytes, 1, 256);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => produce);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(writer.CompleteAsync);
        Assert.Equal(0, writer.QueuedBytes);
        async Task Produce() { for (var i = 0; i < 100; i++) await writer.EmitAsync(Event("response.output_text.delta")); }
    }

    [Fact]
    public async Task OversizeEvent_FailsExplicitlyBeforeResponseStarts()
    {
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await using var writer = new SsePushResponseWriter(context.Response, default, new() { MaxEventBytes = 32 });
        var payload = Event("response.output_text.delta"); payload["delta"] = new string('x', 100);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.EmitAsync(payload));
        Assert.False(context.Response.HasStarted);
        Assert.Equal(0, writer.QueuedBytes);
    }

    [Fact]
    public async Task LoopbackHttp_IdleFlushAndFirstEventArriveBeforeProducerFinishes()
    {
        var released = Signal(); var emitted = Signal();
        await WithServer(async context =>
        {
            await using var writer = new SsePushResponseWriter(context.Response, context.RequestAborted);
            await writer.EmitAsync(Event("response.created"));
            await writer.EmitAsync(Event("response.output_text.delta"));
            emitted.TrySetResult();
            await released.Task.WaitAsync(context.RequestAborted);
            await writer.EmitAsync(Event("response.completed"));
            await writer.CompleteAsync();
        }, async uri =>
        {
            using var http = new HttpClient();
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("response.created", await ReadEvent(reader));
            // Producer is idle, so this MUST be the reusable timer, not the next event.
            Assert.Contains("response.output_text.delta", await ReadEvent(reader));
            Assert.False(released.Task.IsCompleted);
            released.TrySetResult();
            Assert.Contains("response.completed", await ReadEvent(reader));
            Assert.Equal("", await reader.ReadToEndAsync());
        });
    }

    [Fact]
    public async Task LoopbackHttp_DisconnectedSlowReaderCancelsProducerAndDrainsQueue()
    {
        var ended = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithServer(async context =>
        {
            await using var writer = new SsePushResponseWriter(context.Response, context.RequestAborted,
                new() { MaxQueuedEvents = 2, MaxQueuedBytes = 32 * 1024 });
            try
            {
                await writer.EmitAsync(Event("response.created"));
                var delta = Event("response.output_text.delta"); delta["delta"] = new string('x', 16 * 1024);
                for (var i = 0; i < 100_000; i++) await writer.EmitAsync(delta);
                await writer.CompleteAsync();
                ended.TrySetException(new Exception("Expected peer disconnect, not successful completion."));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                try { await writer.CompleteAsync(); } catch (Exception) { }
                ended.TrySetResult(writer.QueuedBytes);
            }
        }, async uri =>
        {
            using (var http = new HttpClient())
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.Contains("response.created", await ReadEvent(reader));
                // Deliberately stop reading a much larger stream, then close the real socket.
            }
            Assert.Equal(0, await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    [Fact]
    public async Task LoopbackWebSocket_TypedAdapterPublishesBeforeSourceFinishesAndLateFailureIsNotSuccess()
    {
        var release = Signal(); var ended = Signal();
        await WithServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var writer = new MultiAgentModelStreamWriter(async (payload, ct) =>
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct));
            try { await writer.WriteFramesAsync(Source(), static _ => false, static () => 0, context.RequestAborted); ended.TrySetResult(); }
            catch (Exception error) { ended.TrySetException(error); }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "finished", context.RequestAborted);
        }, async uri =>
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, default);
            Assert.Contains("response.created", await Receive(socket));
            Assert.False(ended.Task.IsCompleted);
            release.TrySetResult();
            var failed = await Receive(socket);
            Assert.Contains("response.failed", failed); Assert.DoesNotContain("response.completed", failed);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }, websocket: true);
        async IAsyncEnumerable<SseFrame> Source()
        {
            yield return SseFrame.FromJson(Event("response.created"), "response.created");
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            yield return SseFrame.FromJson(new D { ["type"]="response.failed", ["response"]=new D { ["id"]="r1", ["status"]="failed", ["output"]=new List<object?>() } }, "response.failed");
        }
    }

    [Fact]
    public async Task LoopbackWebSocket_DisconnectedSlowReaderDisposesSource()
    {
        var ended = Signal();
        var disposed = false;
        await WithServer(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var writer = new MultiAgentModelStreamWriter(async (payload, ct) =>
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, ct));
            try
            {
                await writer.WriteFramesAsync(Source(), static _ => false, static () => 0, context.RequestAborted);
                ended.TrySetException(new Exception("Expected disconnect, not success."));
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException)
            {
                if (disposed) ended.TrySetResult();
                else ended.TrySetException(new Exception("Source was not disposed after downstream failure."));
            }
        }, async uri =>
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, default);
            Assert.Contains("response.created", await Receive(socket));
            // Stop reading before abort: exercise real socket buffers, not a MemoryStream.
            socket.Abort();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }, websocket: true);
        async IAsyncEnumerable<SseFrame> Source()
        {
            try
            {
                yield return SseFrame.FromJson(Event("response.created"), "response.created");
                var frame = SseFrame.FromJson(new D { ["type"]="response.output_text.delta", ["delta"]=new string('x',16*1024) }, "response.output_text.delta");
                for (var i = 0; i < 100_000; i++) { yield return frame; await Task.CompletedTask; }
            }
            finally { disposed = true; }
        }
    }

    private static D Event(string type) => new() { ["type"] = type };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<string> ReadEvent(StreamReader reader)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
        { if (line.Length == 0) return text.ToString(); text.AppendLine(line); }
        throw new EndOfStreamException();
    }
    private static async Task<string> Receive(ClientWebSocket socket)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        WebSocketReceiveResult read;
        do { read = await socket.ReceiveAsync(buffer, stop.Token); bytes.Write(buffer, 0, read.Count); } while (!read.EndOfMessage);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static async Task WithServer(RequestDelegate handler, Func<Uri, Task> client, bool websocket = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        if (websocket) app.UseWebSockets();
        app.Run(handler);
        await app.StartAsync();
        try { await client(new Uri(app.Urls.Single())).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(stop.Token); }
    }
    private sealed class CountingStream : MemoryStream
    { public int Flushes; public override Task FlushAsync(CancellationToken ct) { Flushes++; return base.FlushAsync(ct); } }
    private sealed class TerminalFailureStream : MemoryStream
    {
        public override Task FlushAsync(CancellationToken ct) => Encoding.UTF8.GetString(ToArray()).Contains("response.completed")
            ? Task.FromException(new IOException("terminal flush fault")) : Task.CompletedTask;
    }
    private sealed class BlockedFlushStream : MemoryStream
    {
        public TaskCompletionSource Entered = Signal();
        public override async Task FlushAsync(CancellationToken ct) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
    }
}
