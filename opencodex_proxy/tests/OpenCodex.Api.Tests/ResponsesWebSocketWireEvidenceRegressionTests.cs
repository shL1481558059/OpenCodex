using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;
using Xunit.Abstractions;
using CoreUpstreamException = OpenCodex.Core.Errors.UpstreamException;
using ProtocolUpstreamException = OpenCodex.ProtocolConversion.Errors.UpstreamException;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class ResponsesWebSocketWireEvidenceRegressionTests
{
    private static readonly TestParameters Settings = TestParameters.Load();
    private static readonly ProtocolConversionParameters Root = ProtocolConversionParameters.Current;
    private const string WebSocketWireFormat = "responses_websocket_text_json_utf8";
    private const string SseWireFormat = "sse_frame_utf8";

    public ResponsesWebSocketWireEvidenceRegressionTests(ITestOutputHelper output)
    {
        output.WriteLine(
            "Root P={0} C={1} E={2} M={3}; Fixture C={4} E={5} M={6}, fragment={7}, timeout_ms={8}. Fixture M derives from fixture E and actual root M/E; no root override.",
            Root.MaxRequestBodyBytes, Root.SseMaxCaptureBytes,
            Root.SseMaxEventBytes, Root.SseMaxWebSocketMessageBytes, Settings.CaptureBytes,
            Settings.EventBytes, Settings.MessageBytes, Settings.FragmentBytes, Settings.CaseTimeoutMilliseconds);
    }

    [Fact]
    public async Task FailureEvidencePreservesActualFragmentedWebSocketBytesAfterReceiveBufferReuseAndLeaseDisposal()
    {
        using var fixture = new Fixture();
        // The intrinsic int64 record header, not a new business capacity setting, fills C exactly.
        var firstRaw = PadJson(RawResponse("response.created", "首条中<>&𝄞"), Settings.CaptureBytes - sizeof(long));
        var lastRaw = RawResponse("response.completed", "末条é<>&中𝄞");
        await using var source = await RawWebSocketSource.StartAsync([firstRaw, lastRaw], fixture.Token);
        var frames = await ReceiveFramesAsync(source, fixture.Token);
        Assert.Equal(2, frames.Count);
        AssertDefaultSemanticSse(frames[0], firstRaw);
        AssertDefaultSemanticSse(frames[1], lastRaw);
        Assert.Equal("resp-原<>&", JsonDictionaryValue.String(
            Assert.IsType<D>(Assert.IsType<D>(frames[0].Event!.Data)["response"]), "id"));
        Assert.True(frames[1].IsSuccessfulResponseCompleted);
        Assert.Equal(0, source.HttpFallbackCalls);

        // Both messages have already been received and the actual pool/lease disposed. A view of
        // the reusable receive MemoryStream would now be empty or overwritten by the last message.
        var capture = fixture.Capture(Settings.MessageBytes);
        capture.RecordReceivedFrame(frames[0]);
        capture.Accept(frames[0], evidenceAlreadyRecorded: true);
        Assert.False(capture.NeedsFailureEvidence);
        var failure = Assert.Throws<ProtocolUpstreamException>(() => capture.Accept(frames[1]));
        Assert.Equal("evidence_capture_limit", failure.ErrorCode);
        Assert.Equal(507, failure.StatusCode);
        Assert.False(capture.IsComplete);
        var evidence = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        AssertEvidence(evidence, [firstRaw, lastRaw], Settings.MessageBytes, WebSocketWireFormat, true, fixture.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task AcceptedRawMessageAtMIsJournaledEvenWhenDefaultReconstructedSseExceedsM(int boundaryOffset)
    {
        using var fixture = new Fixture();
        var raw = EscapingHeavyResponse(Settings.MessageBytes + boundaryOffset);
        Assert.InRange(raw.Length, 1, Settings.MessageBytes);
        await using var source = await RawWebSocketSource.StartAsync([raw], fixture.Token);
        var frame = Assert.Single(await ReceiveFramesAsync(source, fixture.Token));
        AssertDefaultSemanticSse(frame, raw);
        Assert.True(frame.Utf8Length > Settings.MessageBytes,
            "The fixture must prove that default semantic SSE expansion exceeds the accepted raw WS message limit.");
        Assert.True(frame.Utf8Length > Settings.EventBytes);
        Assert.Equal(0, source.HttpFallbackCalls);

        var capture = fixture.Capture(Settings.MessageBytes);
        var failure = Assert.Throws<ProtocolUpstreamException>(() => capture.Accept(frame));
        Assert.Equal("evidence_capture_limit", failure.ErrorCode);
        // The complete accepted message, not reconstructed SSE, must survive the intentional C failure.
        var evidence = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        AssertEvidence(evidence, [raw], Settings.MessageBytes, WebSocketWireFormat, true, fixture.Token);
        Assert.Equal(checked((long)Settings.CaptureBytes + Settings.MessageBytes + sizeof(long)),
            ReceivedFrameEvidence.MaxJournalBytes(Settings.CaptureBytes, Settings.MessageBytes));
        Assert.Equal(checked((long)Root.SseMaxCaptureBytes + Root.SseMaxWebSocketMessageBytes + sizeof(long)),
            ReceivedFrameEvidence.MaxJournalBytes(Root.SseMaxCaptureBytes, Root.SseMaxWebSocketMessageBytes));
    }

    [Fact]
    public async Task DownstreamDefaultEncoderStillRejectsAtEWhileFailureEvidenceKeepsAcceptedRawWebSocketMessage()
    {
        using var fixture = new Fixture();
        var raw = EscapingHeavyResponse(Settings.CaptureBytes - sizeof(long));
        await using var source = await RawWebSocketSource.StartAsync([raw], fixture.Token);
        var frame = Assert.Single(await ReceiveFramesAsync(source, fixture.Token));
        AssertDefaultSemanticSse(frame, raw);
        var payload = Assert.IsType<D>(frame.Event!.Data);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(payload).Length > Settings.EventBytes);
        Assert.True(raw.Length < Settings.MessageBytes);
        var capture = fixture.Capture(Settings.MessageBytes);
        capture.RecordReceivedFrame(frame);
        using var downstream = new NoSendSocket();
        await Assert.ThrowsAsync<InvalidDataException>(() => WebSocketJsonWriter.SendAsync(
            downstream, payload, Settings.EventBytes, fixture.Token));
        Assert.Equal(0, downstream.SendAttempts);
        Assert.True(capture.NeedsFailureEvidence);
        Assert.False(capture.IsComplete);
        var evidence = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        AssertEvidence(evidence, [raw], Settings.MessageBytes, WebSocketWireFormat, false, fixture.Token);
        Assert.Equal(0, source.HttpFallbackCalls);
    }

    [Fact]
    public async Task RawMessageOverMIsRejectedWithoutClaimingUndeliveredNetworkFragmentsAsCompleteEvidence()
    {
        using var fixture = new Fixture();
        var raw = PadJson(RawResponse("response.completed", "未接受中<>&"), Settings.MessageBytes + 1);
        await using var source = await RawWebSocketSource.StartAsync([raw], fixture.Token);
        await using var pool = NewPool();
        await using var lease = await pool.AcquireAsync("wire-evidence-over-M", Channel(source.BaseUrl),
            Settings.TimeoutSeconds, fixture.Token);
        await lease.SendAsync(Request(), fixture.Token);
        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(async () =>
        {
            _ = await lease.ReceiveAsync(Settings.TimeoutSeconds, fixture.Token);
        });
        Assert.Equal(502, failure.StatusCode);
        lease.Abort();
        var capture = fixture.Capture(Settings.MessageBytes);
        var evidence = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        // The reader rejected this message before delivering a parsed frame. Even if the mock
        // server offered EOM, an accepted-frame journal cannot invent full rejected-message bytes.
        using var json = JsonDocument.Parse(WriteJson(evidence.Response, fixture.Token));
        var metadata = json.RootElement.GetProperty("_opencodex_capture");
        Assert.Equal(0, metadata.GetProperty("record_count").GetInt64());
        Assert.Equal(0, metadata.GetProperty("received_bytes").GetInt64());
        Assert.False(metadata.GetProperty("source_reconstructed").GetBoolean());
        Assert.Empty(Convert.FromBase64String(json.RootElement.GetProperty("received_utf8_base64").GetString()!));
    }

    [Fact]
    public void UnassociatedParserDeliveredSourceSseKeepsItsExistingFrameWriteUtf8Evidence()
    {
        using var fixture = new Fixture();
        var firstWire = SseWire("response.created", "首条中<>&𝄞");
        var firstLength = Encoding.UTF8.GetByteCount(firstWire);
        var padding = Settings.CaptureBytes - sizeof(long) - firstLength;
        Assert.True(padding >= 0);
        firstWire = firstWire[..^2] + new string(' ', padding) + "\n\n";
        var first = Assert.Single(new SseFrameReader(new SseFrameOptions { MaxEventBytes = Settings.EventBytes }).Feed(firstWire));
        var last = Assert.Single(new SseFrameReader(new SseFrameOptions { MaxEventBytes = Settings.EventBytes })
            .Feed(SseWire("response.completed", "末条é<>&中")));
        var firstOutput = new ArrayBufferWriter<byte>();
        Assert.Equal(first.Utf8Length, first.WriteUtf8(firstOutput));
        Assert.Equal(Encoding.UTF8.GetBytes(first.WireText), firstOutput.WrittenSpan.ToArray());
        var capture = fixture.Capture(Settings.EventBytes);
        capture.Accept(first);
        var failure = Assert.Throws<ProtocolUpstreamException>(() => capture.Accept(last));
        Assert.Equal("evidence_capture_limit", failure.ErrorCode);
        var evidence = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        AssertEvidence(evidence, [Encoding.UTF8.GetBytes(first.WireText), Encoding.UTF8.GetBytes(last.WireText)],
            Settings.EventBytes, SseWireFormat, true, fixture.Token);
    }

    private static async Task<List<SseFrame>> ReceiveFramesAsync(RawWebSocketSource source, CancellationToken token)
    {
        await using var pool = NewPool();
        var http = new NoFallbackHttpClient();
        using var transport = new ResponsesUpstreamTransport(http, pool, NullLogger<ResponsesUpstreamTransport>.Instance);
        var request = Request();
        var channel = Channel(source.BaseUrl);
        var route = new ProxyRouteDto(channel, "wire-test", "wire-test", false, true);
        var context = new ProxyStreamContext(0, Guid.NewGuid(), Guid.NewGuid().ToString("N"), "wire-test", Guid.NewGuid(),
            WebSearchPayload.DeepCopyObject(request), WebSearchPayload.DeepCopyObject(request), WebSearchPayload.DeepCopyObject(request),
            ProtocolConverter.Responses, route, ProtocolConverter.Responses, "wire-source", "admin", "wire-test", "wire-test",
            Settings.TimeoutSeconds, new ProxyRequestMetadata("GET", "/v1/responses", "127.0.0.1",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["thread-id"] = Guid.NewGuid().ToString("N") }),
            new NoopWriter(), token);
        var frames = new List<SseFrame>();
        await foreach (var frame in transport.StreamEventsAsync(context, token)) frames.Add(frame);
        await source.AllMessagesSent.WaitAsync(token);
        source.HttpFallbackCalls = http.Calls;
        Assert.Equal("response.create", JsonDictionaryValue.String(await source.RequestReceived.WaitAsync(token), "type"));
        return frames;
    }

    private static ResponsesWebSocketPool NewPool() => new(new SseTransportOptions
    {
        MaxEventBytes = Settings.EventBytes, MaxWebSocketMessageBytes = Settings.MessageBytes
    });

    private static D Channel(string baseUrl) => new(StringComparer.Ordinal)
    {
        ["id"] = "wire-source", ["name"] = "wire-source", ["type"] = ProtocolConverter.Responses,
        ["baseurl"] = baseUrl + "/v1", ["auth_mode"] = "none", ["headers"] = new D(),
        ["timeout_seconds"] = Settings.TimeoutSeconds, ["compat"] = new D { ["ws_mode"] = "passthrough" }
    };

    private static D Request() => new(StringComparer.Ordinal)
    {
        ["model"] = "wire-test", ["store"] = false, ["stream"] = true,
        ["input"] = new List<object?> { new D { ["role"] = "user", ["content"] = "wire evidence" } }
    };

    private static byte[] RawResponse(string type, string literal) => Encoding.UTF8.GetBytes(
        "\t {\r\n  \"type\" : \"" + type + "\",\r\n  \"response\" : { \"id\" : \"resp-原<>&\", \"status\" : \""
        + (type == "response.created" ? "in_progress" : "completed")
        + "\", \"output\" : [] },\r\n  \"marker\" : \"" + literal
        + "\", \"literal_escape\" : \"\\u0061\", \"number\" : 1e+0\r\n } \t\r\n");

    private static byte[] PadJson(byte[] json, int length)
    {
        Assert.True(json.Length <= length);
        var raw = new byte[length];
        json.CopyTo(raw, 0);
        raw.AsSpan(json.Length).Fill((byte)' ');
        return raw;
    }

    private static byte[] EscapingHeavyResponse(int length)
    {
        const string pattern = "中<>&";
        var shell = RawResponse("response.completed", "");
        var repeats = (length - shell.Length) / Encoding.UTF8.GetByteCount(pattern);
        Assert.True(repeats > 0);
        var literal = new StringBuilder(checked(repeats * pattern.Length));
        for (var index = 0; index < repeats; index++) literal.Append(pattern);
        return PadJson(RawResponse("response.completed", literal.ToString()), length);
    }

    private static string SseWire(string type, string literal) => "event: " + type
        + "\ndata: { \"type\" : \"" + type + "\", \"response\" : { \"id\" : \"resp-原<>&\", \"status\" : \""
        + (type == "response.created" ? "in_progress" : "completed")
        + "\", \"output\" : [] }, \"marker\" : \"" + literal + "\" }\n\n";

    private static void AssertDefaultSemanticSse(SseFrame frame, byte[] original)
    {
        using var parsed = JsonDocument.Parse(original);
        var expectedPayload = Assert.IsType<D>(WebSearchPayload.FromJsonElement(parsed.RootElement));
        var payload = Assert.IsType<D>(frame.Event!.Data);
        Assert.Equal(JsonSerializer.Serialize(expectedPayload), JsonSerializer.Serialize(payload));
        var type = JsonDictionaryValue.String(payload, "type");
        var expectedWire = "event: " + type + "\ndata: " + JsonSerializer.Serialize(expectedPayload) + "\n\n";
        Assert.Equal(expectedWire, frame.WireText);
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedWire), frame.Utf8Length);
        var output = new ArrayBufferWriter<byte>();
        Assert.Equal(frame.Utf8Length, frame.WriteUtf8(output));
        Assert.Equal(Encoding.UTF8.GetBytes(expectedWire), output.WrittenSpan.ToArray());
        // Evidence belongs to frame metadata, never to the upstream/client semantic JSON object.
        Assert.Equal(expectedPayload.Keys.OrderBy(key => key), payload.Keys.OrderBy(key => key));
    }

    private static void AssertEvidence(ReceivedFrameEvidenceSnapshot snapshot, byte[][] actualMessages,
        int sourceMargin, string wireFormat, bool prefixExceeded, CancellationToken token)
    {
        var bytes = WriteJson(snapshot.Response, token);
        Assert.Equal(bytes.LongLength, ProtocolJsonWriter.Count(snapshot.Response, audit: true, token));
        Assert.Equal(bytes, WriteJson(snapshot.Response, token));
        Assert.InRange(bytes.LongLength, 1L, ReceivedFrameEvidence.MaxJsonBytes(Settings.CaptureBytes, sourceMargin));
        using var json = JsonDocument.Parse(bytes);
        var metadata = json.RootElement.GetProperty("_opencodex_capture");
        Assert.Equal("received_frames", metadata.GetProperty("representation").GetString());
        Assert.Equal(wireFormat, metadata.GetProperty("wire_format").GetString());
        Assert.Equal("parser_delivered_accepted_frames", metadata.GetProperty("evidence_boundary").GetString());
        Assert.Equal("int64_le_length_prefixed_utf8", metadata.GetProperty("framing").GetString());
        Assert.False(metadata.GetProperty("source_reconstructed").GetBoolean());
        Assert.False(metadata.GetProperty("completed").GetBoolean());
        Assert.False(metadata.GetProperty("truncated").GetBoolean());
        Assert.Equal(prefixExceeded, metadata.GetProperty("prefix_limit_exceeded").GetBoolean());
        Assert.Equal(Settings.CaptureBytes, metadata.GetProperty("prefix_limit_bytes").GetInt32());
        Assert.Equal(sourceMargin, metadata.GetProperty("overflow_margin_bytes").GetInt32());
        Assert.Equal(sizeof(long), metadata.GetProperty("length_header_bytes").GetInt32());
        Assert.Equal(actualMessages.LongLength, metadata.GetProperty("record_count").GetInt64());
        var journal = Convert.FromBase64String(json.RootElement.GetProperty("received_utf8_base64").GetString()!);
        var expectedPayloadBytes = actualMessages.Sum(message => message.LongLength);
        var expectedJournalBytes = checked(expectedPayloadBytes + actualMessages.LongLength * sizeof(long));
        Assert.Equal(expectedPayloadBytes, metadata.GetProperty("received_bytes").GetInt64());
        Assert.Equal(expectedJournalBytes, metadata.GetProperty("journal_bytes").GetInt64());
        Assert.Equal(expectedJournalBytes, journal.LongLength);
        Assert.InRange(journal.LongLength, 0L, ReceivedFrameEvidence.MaxJournalBytes(Settings.CaptureBytes, sourceMargin));
        var offset = 0;
        foreach (var actual in actualMessages)
        {
            Assert.Equal(actual.LongLength, BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(offset, sizeof(long))));
            offset += sizeof(long);
            Assert.Equal(actual, journal.AsSpan(offset, actual.Length).ToArray());
            offset += actual.Length;
        }
        Assert.Equal(journal.Length, offset);
    }

    private static byte[] WriteJson(object? value, CancellationToken token)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            ProtocolJsonWriter.Write(writer, value, 0, token);
            writer.Flush();
        }
        return output.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _holders = [];
        private readonly CancellationTokenSource _deadline = new(Settings.CaseTimeoutMilliseconds);
        private readonly string _temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string _directory;
        public Fixture()
        {
            _directory = Path.GetFullPath(Path.Combine(_temporaryParent, "ocxp-ws-wire-evidence-" + Guid.NewGuid().ToString("N")));
            Spool = new ProxyAuditSpool(new ProxyAuditSpoolOptions
            {
                Directory = _directory, MemoryThresholdBytes = 0, OrphanAgeSeconds = Settings.OrphanAgeSeconds
            });
            Store = new SnapshotStore(Spool);
            try
            {
                Assert.Equal(Root.FlushBytes, Store.PageBytes);
                Reservation = Store.Reserve(Token);
            }
            catch { Spool.Dispose(); _deadline.Dispose(); DeleteOwnDirectory(); throw; }
        }
        public ProxyAuditSpool Spool { get; }
        public SnapshotStore Store { get; }
        public SnapshotReservation Reservation { get; }
        public CancellationToken Token => _deadline.Token;
        public T Hold<T>(T owner) where T : IDisposable { _holders.Add(owner); return owner; }
        public StreamResponseCapture Capture(int sourceMargin)
        {
            var budget = new StreamCaptureBudget(Settings.CaptureBytes, Root.SseMaxCaptureItems,
                Root.SseMaxCaptureFields, Reservation.AuditCapture, Token);
            try { return Hold(new StreamResponseCapture(ProtocolConverter.Responses, budget, maxEventBytes: sourceMargin)); }
            catch { budget.Dispose(); throw; }
        }
        public void Dispose()
        {
            for (var index = _holders.Count - 1; index >= 0; index--) _holders[index].Dispose();
            Reservation.Dispose(); Spool.Dispose(); _deadline.Dispose();
            Assert.Equal((0L, 0L, 0, 0), Store.Usage);
            DeleteOwnDirectory();
        }
        private void DeleteOwnDirectory()
        {
            Assert.Equal(_temporaryParent, Path.GetDirectoryName(_directory));
            Assert.StartsWith("ocxp-ws-wire-evidence-", Path.GetFileName(_directory));
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RawWebSocketSource : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly byte[][] _messages;
        private readonly TaskCompletionSource<D> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RawWebSocketSource(WebApplication app, byte[][] messages) { _app = app; _messages = messages; }
        public string BaseUrl => _app.Urls.Single();
        public Task<D> RequestReceived => _request.Task;
        public Task AllMessagesSent => _sent.Task;
        public int HttpFallbackCalls { get; set; }
        public static async Task<RawWebSocketSource> StartAsync(byte[][] messages, CancellationToken token)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var source = new RawWebSocketSource(app, messages.Select(message => message.ToArray()).ToArray());
            app.UseWebSockets();
            app.Map("/v1/responses", source.HandleAsync);
            try { await app.StartAsync(token); return source; }
            catch { await app.DisposeAsync(); throw; }
        }
        private async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                var buffer = new byte[Settings.FragmentBytes];
                using var request = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    request.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);
                using (var parsed = JsonDocument.Parse(request.ToArray()))
                    _request.TrySetResult(Assert.IsType<D>(WebSearchPayload.FromJsonElement(parsed.RootElement)));
                foreach (var raw in _messages)
                {
                    // Deliberately fragment inside the first non-ASCII UTF-8 scalar. One extra
                    // byte is an encoding-boundary probe, not a business buffer-size constant.
                    var scalar = Array.FindIndex(raw, value => value >= 0x80);
                    var offset = 0;
                    if (scalar >= 0 && scalar + 1 < raw.Length)
                    {
                        var split = scalar + 1;
                        await socket.SendAsync(raw.AsMemory(0, split), WebSocketMessageType.Text, false, context.RequestAborted);
                        offset = split;
                    }
                    while (offset < raw.Length)
                    {
                        var count = Math.Min(Settings.FragmentBytes, raw.Length - offset);
                        await socket.SendAsync(raw.AsMemory(offset, count), WebSocketMessageType.Text,
                            offset + count == raw.Length, context.RequestAborted);
                        offset += count;
                    }
                }
                _sent.TrySetResult();
                while (socket.State == WebSocketState.Open)
                {
                    received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                }
            }
            catch (WebSocketException) { /* Pool abort/disposal is intentional after these source fixtures. */ }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            { _request.TrySetException(exception); _sent.TrySetException(exception); throw; }
        }
        public async ValueTask DisposeAsync()
        { await _app.StopAsync(); await _app.DisposeAsync(); }
    }

    private sealed class NoFallbackHttpClient : IUpstreamClient
    {
        public int Calls { get; private set; }
        public Task<D> PostJsonAsync(IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, CancellationToken cancellationToken)
        { Calls++; throw new Xunit.Sdk.XunitException("Raw WS evidence must not fall back to HTTP."); }
        public IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload, int defaultTimeout, CancellationToken cancellationToken)
        { Calls++; throw new Xunit.Sdk.XunitException("Raw WS evidence must not fall back to HTTP."); }
    }

    private sealed class NoopWriter : IProxyStreamWriter
    {
        public void PrepareSse() { }
        public Task<StreamWriteMetrics> WriteLinesAsync(IAsyncEnumerable<string> lines, Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default) => Task.FromResult(new StreamWriteMetrics());
    }

    private sealed class NoSendSocket : WebSocket
    {
        public int SendAttempts { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        { SendAttempts++; throw new Xunit.Sdk.XunitException("Default E preflight must fail before any downstream fragment."); }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override void Abort() { }
        public override void Dispose() { }
    }

    private sealed record TestParameters(int CaptureBytes, int EventBytes, int FragmentBytes,
        int CaseTimeoutMilliseconds, int OrphanAgeSeconds)
    {
        public int MessageBytes => checked((int)((long)EventBytes * ProtocolConversionParameters.Current.SseMaxWebSocketMessageBytes
            / ProtocolConversionParameters.Current.SseMaxEventBytes));
        // Millisecond/second conversion is intrinsic time-unit arithmetic, not an injected timeout policy.
        public int TimeoutSeconds => checked((int)((CaseTimeoutMilliseconds + 999L) / 1000L));
        public static TestParameters Load() => new(
            WsHistoryTestParameters.OutputCaptureMaxBytes,
            WsHistoryTestParameters.OutputEventMaxBytes,
            WsHistoryTestParameters.ReceiveFragmentBytes,
            WsHistoryTestParameters.CaseTimeoutMilliseconds,
            WsHistoryTestParameters.SnapshotOrphanAgeSeconds);
    }
}
