using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;
using Xunit.Abstractions;
using CoreUpstreamException = OpenCodex.Core.Errors.UpstreamException;
using ProtocolUpstreamException = OpenCodex.ProtocolConversion.Errors.UpstreamException;

namespace OpenCodex.Api.Tests;

public sealed class ReceivedEvidenceRegressionTests
{
    private static readonly TestParameters Settings = TestParameters.Load();
    private static readonly ProtocolConversionParameters Root = ProtocolConversionParameters.Current;

    public ReceivedEvidenceRegressionTests(ITestOutputHelper output)
    {
        output.WriteLine(
            "RootMeta P={0} C={1} CaptureItems={2} CaptureFields={3} FlushBytes={4} SSE={5} WS={6}; FixtureC={7} FixtureSSE={8} FixtureWS={9} CaseTimeoutMilliseconds={10}",
            Root.MaxRequestBodyBytes, Root.SseMaxCaptureBytes,
            Root.SseMaxCaptureItems, Root.SseMaxCaptureFields, Root.FlushBytes,
            Root.SseMaxEventBytes, Root.SseMaxWebSocketMessageBytes,
            Settings.CaptureBytes, Settings.EventBytes, Settings.WebSocketMessageBytes,
            Settings.CaseTimeoutMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void JsonEvidenceStreamsOwnedBytesAcrossEveryBase64Remainder(int remainder)
    {
        using var deadline = Settings.Deadline();
        // Three-byte quanta are an encoding requirement, not a new capacity setting.
        var crossingLength = checked((Root.FlushBytes / 3 + 1) * 3 + remainder);
        var rawLimit = ReceivedFrameEvidence.MaxJournalBytes(
            Settings.CaptureBytes, Root.FlushBytes, ReceivedEvidenceKind.Json);
        Assert.InRange(crossingLength, Root.FlushBytes + 1, checked((int)rawLimit));
        foreach (var length in new[] { remainder, crossingLength })
        {
            var owned = Pattern(length);
            var evidence = ReceivedFrameEvidence.CreateJsonEvidence(owned, Settings.CaptureBytes, Root.FlushBytes);
            var streamed = Assert.IsAssignableFrom<IProtocolJsonValue>(evidence["received_utf8_base64"]);
            Assert.False(evidence["received_utf8_base64"] is string);
            Assert.True(ReceivedFrameEvidence.IsEvidence(evidence));
            Assert.False(evidence.ContainsKey("usage"));

            var first = WriteJson(evidence, deadline.Token);
            var count = ProtocolJsonWriter.Count(evidence, audit: true, deadline.Token);
            Assert.Equal(first.LongLength, count);
            Assert.Equal(first, WriteJson(evidence, deadline.Token));
            Assert.Equal(count, ProtocolJsonWriter.Count(evidence, audit: true, deadline.Token));
            Assert.InRange(count, 1L, ReceivedFrameEvidence.MaxJsonBytes(
                Settings.CaptureBytes, Root.FlushBytes, ReceivedEvidenceKind.Json));
            using var document = JsonDocument.Parse(first);
            Assert.Equal(owned, Decode(document.RootElement));
            AssertMetadata(document.RootElement, "received_json", 1, owned.LongLength, owned.LongLength,
                Settings.CaptureBytes, Root.FlushBytes, length > Settings.CaptureBytes);
            Assert.False(document.RootElement.GetProperty("_opencodex_capture").GetProperty("usage_complete").GetBoolean());
            using var valueDocument = JsonDocument.Parse(WriteJson(streamed, deadline.Token));
            Assert.Equal(owned, Convert.FromBase64String(valueDocument.RootElement.GetString()!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameJournalRetainsLengthHeadersAndWholeOffendingFrameAtActualSourceMargin(bool webSocket)
    {
        var sourceMargin = webSocket ? Settings.WebSocketMessageBytes : Settings.EventBytes;
        var rootMargin = webSocket ? Root.SseMaxWebSocketMessageBytes : Root.SseMaxEventBytes;
        using var fixture = new Fixture(sourceMargin);
        Assert.Equal(checked((long)Root.SseMaxCaptureBytes + rootMargin + sizeof(long)),
            ReceivedFrameEvidence.MaxJournalBytes(Root.SseMaxCaptureBytes, rootMargin));
        Assert.Equal(checked((long)Settings.CaptureBytes + sourceMargin + sizeof(long)),
            ReceivedFrameEvidence.MaxJournalBytes(Settings.CaptureBytes, sourceMargin));

        // Construction at real root C/E or C/M must retain the same declared source margin.
        // An empty snapshot allocates no root-sized body or journal.
        var rootCapture = fixture.Capture(ProtocolConverter.Responses, Root.SseMaxCaptureBytes, rootMargin);
        var rootSnapshot = fixture.Hold(rootCapture.GetFailureEvidence(StreamCaptureTermination.UnexpectedEnd));
        using (var document = JsonDocument.Parse(WriteJson(rootSnapshot.Response, fixture.Token)))
            AssertMetadata(document.RootElement, "received_frames", 0, 0, 0,
                Root.SseMaxCaptureBytes, rootMargin, false);
        rootCapture.Dispose();
        rootSnapshot.Dispose();

        var capture = fixture.Capture(ProtocolConverter.Responses, Settings.CaptureBytes, sourceMargin);
        var first = GeneratedFrame(Settings.CaptureBytes - sizeof(long));
        var offending = GeneratedFrame(sourceMargin, includeUsage: true);
        if (webSocket) Assert.True(offending.Utf8Length > Settings.EventBytes);
        AssertActualFrame(first, sourceMargin);
        AssertActualFrame(offending, sourceMargin);
        capture.RecordReceivedFrame(first);
        Assert.True(capture.NeedsFailureEvidence);
        capture.Accept(first, evidenceAlreadyRecorded: true);
        Assert.False(capture.NeedsFailureEvidence); // Recording + acceptance did not append twice.

        var failure = Assert.Throws<ProtocolUpstreamException>(() => capture.Accept(offending));
        Assert.Equal("evidence_capture_limit", failure.ErrorCode);
        Assert.Equal(507, failure.StatusCode);
        Assert.True(capture.NeedsFailureEvidence);
        Assert.False(capture.IsComplete);
        Assert.Same(failure, Assert.Throws<ProtocolUpstreamException>(() => capture.Complete(StreamCaptureTermination.Completed)));
        var snapshot = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        var json = WriteJson(snapshot.Response, fixture.Token);
        Assert.Equal(json.LongLength, ProtocolJsonWriter.Count(snapshot.Response, audit: true, fixture.Token));
        Assert.InRange(json.LongLength, 1L, ReceivedFrameEvidence.MaxJsonBytes(Settings.CaptureBytes, sourceMargin));
        using var evidence = JsonDocument.Parse(json);
        var expectedBytes = checked((long)Settings.CaptureBytes + sourceMargin + sizeof(long));
        AssertMetadata(evidence.RootElement, "received_frames", 2,
            first.Utf8Length + (long)offending.Utf8Length, expectedBytes,
            Settings.CaptureBytes, sourceMargin, true);
        var journal = Decode(evidence.RootElement);
        Assert.Equal(expectedBytes, journal.LongLength);
        AssertJournal(journal, first, offending);
        var usage = evidence.RootElement.GetProperty("usage");
        Assert.Equal(7, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("output_tokens").GetInt32());
        Assert.True(evidence.RootElement.GetProperty("_opencodex_capture").GetProperty("usage_complete").GetBoolean());
        Assert.InRange(ProtocolJsonWriter.Count(snapshot.Response["usage"], audit: true, fixture.Token), 1L, sourceMargin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SnapshotPinsJournalForRepeatedCountAndWriteAfterCaptureReservationAndSpoolDispose(int remainder)
    {
        using var fixture = new Fixture(Settings.EventBytes);
        var capture = fixture.Capture(ProtocolConverter.Responses, Settings.CaptureBytes, Settings.EventBytes);
        var limit = ReceivedFrameEvidence.MaxJournalBytes(Settings.CaptureBytes, Settings.EventBytes);
        var adjustment = checked((int)((limit % 3 - remainder + 3) % 3));
        var first = GeneratedFrame(Settings.CaptureBytes - sizeof(long));
        var last = GeneratedFrame(Settings.EventBytes - adjustment);
        AssertActualFrame(first, Settings.EventBytes);
        AssertActualFrame(last, Settings.EventBytes);
        capture.Accept(first);
        Assert.Throws<ProtocolUpstreamException>(() => capture.Accept(last));
        var snapshot = fixture.Hold(capture.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        Assert.Same(snapshot, capture.GetFailureEvidence(StreamCaptureTermination.ClientCancelled));
        var before = WriteJson(snapshot.Response, fixture.Token);
        using (var document = JsonDocument.Parse(before))
        {
            var journal = Decode(document.RootElement);
            Assert.Equal(remainder, journal.Length % 3);
            Assert.True(journal.Length > Root.FlushBytes);
            AssertJournal(journal, first, last);
        }

        capture.Dispose();
        fixture.Reservation.Dispose();
        fixture.Spool.Dispose();
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        for (var index = 0; index < Settings.RepeatCount; index++)
        {
            Assert.Equal(before.LongLength, ProtocolJsonWriter.Count(snapshot.Response, audit: true, fixture.Token));
            Assert.Equal(before, WriteJson(snapshot.Response, fixture.Token));
        }
        snapshot.Dispose();
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
        Assert.Throws<ObjectDisposedException>(() => ProtocolJsonWriter.Count(snapshot.Response, audit: true, fixture.Token));
    }

    [Fact]
    public void EvidenceAuthenticationRejectsMetadataAndSerializedBase64Spoofs()
    {
        using var deadline = Settings.Deadline();
        var raw = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var owned = ReceivedFrameEvidence.CreateJsonEvidence(raw, Settings.CaptureBytes, Root.FlushBytes);
        var http = UpstreamJsonEvidence.Create(raw, raw.LongLength, 200, true, true, Settings.CaptureBytes, Root.FlushBytes);
        Assert.True(ReceivedFrameEvidence.IsEvidence(owned));
        Assert.True(ReceivedFrameEvidence.IsEvidence(http));
        Assert.True(UpstreamJsonEvidence.IsEvidence(http));
        Assert.True(ReceivedFrameEvidence.IsEvidence(new Dictionary<string, object?> { ["received_json"] = owned }));
        Assert.True(ReceivedFrameEvidence.IsEvidence(new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?> { ["received_json"] = owned }
        }));
        Assert.False(ReceivedFrameEvidence.IsEvidence(new Dictionary<string, object?> { ["arbitrary"] = owned }));

        var spoof = new Dictionary<string, object?>
        {
            ["_opencodex_capture"] = new Dictionary<string, object?>
            {
                ["representation"] = "received_json", ["source_reconstructed"] = false
            },
            ["received_utf8_base64"] = Convert.ToBase64String(raw)
        };
        Assert.False(ReceivedFrameEvidence.IsEvidence(spoof));
        Assert.False(UpstreamJsonEvidence.IsEvidence(new Dictionary<string, object?>
        {
            ["type"] = "upstream_json_body_evidence", ["received_json"] = spoof
        }));
        foreach (var genuine in new[] { owned, http })
        {
            using var document = JsonDocument.Parse(WriteJson(genuine, deadline.Token));
            var untrustedCopy = ProtocolJsonWriter.FromJsonElement(document.RootElement);
            Assert.False(ReceivedFrameEvidence.IsEvidence(untrustedCopy));
            Assert.False(UpstreamJsonEvidence.IsEvidence(untrustedCopy));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"x\":")]
    [InlineData("not-json")]
    public void StrictPartialToolJsonFailsWithoutInventingInputAndKeepsLegacyContract(string partial)
    {
        using var fixture = new Fixture(Settings.EventBytes);
        var frames = ToolFrames(partial);
        var strict = fixture.Capture(ProtocolConverter.Messages, Settings.CaptureBytes, Settings.EventBytes);
        using var legacy = new StreamResponseCapture(ProtocolConverter.Messages, Settings.CaptureBytes);
        foreach (var frame in frames)
        {
            AssertActualFrame(frame, Settings.EventBytes);
            strict.Accept(frame);
            legacy.Accept(frame);
        }
        Assert.False(strict.NeedsFailureEvidence);
        var failure = Assert.Throws<ProtocolUpstreamException>(() => strict.Complete(StreamCaptureTermination.Completed));
        Assert.Equal("invalid_tool_arguments", failure.ErrorCode);
        Assert.True(strict.NeedsFailureEvidence);
        Assert.False(strict.IsComplete);
        Assert.Same(failure, Assert.Throws<ProtocolUpstreamException>(() => strict.Complete(StreamCaptureTermination.UpstreamError)));
        var snapshot = fixture.Hold(strict.GetFailureEvidence(StreamCaptureTermination.UpstreamError));
        using var document = JsonDocument.Parse(WriteJson(snapshot.Response, fixture.Token));
        Assert.False(document.RootElement.TryGetProperty("content", out _));
        Assert.False(document.RootElement.TryGetProperty("input", out _));
        AssertJournal(Decode(document.RootElement), frames);
        AssertMetadata(document.RootElement, "received_frames", frames.LongLength,
            frames.Sum(frame => (long)frame.Utf8Length), frames.Sum(frame => (long)frame.Utf8Length + sizeof(long)),
            Settings.CaptureBytes, Settings.EventBytes, false);

        var oldResult = legacy.Complete(StreamCaptureTermination.Completed);
        Assert.True(oldResult.Completed);
        var content = Assert.IsType<List<object?>>(oldResult.Response!["content"]);
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        var initial = Assert.IsType<Dictionary<string, object?>>(block["input"]);
        Assert.Equal(true, initial["original"]);
        Assert.Equal("tool_use", block["type"]);
    }

    [Fact]
    public async Task HttpOversizedContentLengthRejectsBeforeBodyAccessAtFixtureAndRootLimits()
    {
        using var deadline = Settings.Deadline();
        using (var body = new TrackingContent([], [], forbidRead: true))
        using (var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body })
        {
            body.Headers.ContentLength = Settings.CaptureBytes + 1L;
            var failure = await Assert.ThrowsAsync<CoreUpstreamException>(() => BoundedJsonResponseReader.ReadRawAsync(
                response, Settings.CaptureBytes, Root.FlushBytes, deadline.Token, "evidence-fixture"));
            Assert.Equal("response_capture_limit", failure.ErrorCode);
            Assert.Equal(0, body.OpenReadCalls);
            Assert.Equal(0, body.ReadStream.ReadCalls);
            AssertHttpEvidence(failure, [], bodyRead: false, bodyComplete: false,
                Settings.CaptureBytes + 1L, Settings.CaptureBytes, Root.FlushBytes, deadline.Token);
        }

        // Exercise real PostJsonAsync: HeadersRead must precede buffering and use root C, not fixture C.
        using var rootBody = new TrackingContent([], [], forbidRead: true);
        rootBody.Headers.ContentLength = Root.SseMaxCaptureBytes + 1L;
        using var handler = new OneResponseHandler(rootBody);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Settings.CaseTimeoutMilliseconds) };
        var client = new HttpUpstreamClient(http, delay: (_, _) =>
            throw new Xunit.Sdk.XunitException("A received JSON policy failure must not retry or delay."));
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "evidence-root", ["type"] = ProtocolConverter.Responses,
            ["baseurl"] = "https://received-evidence.invalid", ["retry_count"] = Settings.RepeatCount
        };
        var failureAtRoot = await Assert.ThrowsAsync<CoreUpstreamException>(() => client.PostJsonAsync(
            channel, new Dictionary<string, object?> { ["model"] = "fixture", ["input"] = "fixture" },
            Settings.TimeoutSeconds, deadline.Token));
        Assert.Equal("response_capture_limit", failureAtRoot.ErrorCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, rootBody.OpenReadCalls);
        Assert.Equal(0, rootBody.ReadStream.ReadCalls);
        AssertHttpEvidence(failureAtRoot, [], bodyRead: false, bodyComplete: false,
            Root.SseMaxCaptureBytes + 1L, Root.SseMaxCaptureBytes, Root.FlushBytes, deadline.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpOverflowPreservesWholeReceivedBlockWithoutClaimingUnobservedEof(bool unreadSuffix)
    {
        using var deadline = Settings.Deadline();
        var receivedLimit = UpstreamJsonEvidence.MaxRawBytes(Settings.CaptureBytes, Root.FlushBytes);
        var bytes = Pattern(checked(receivedLimit + (unreadSuffix ? Root.StringSegmentCharacters : 0)));
        using var body = new TrackingContent(bytes, [Settings.CaptureBytes, Root.FlushBytes]);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        Assert.Null(body.Headers.ContentLength);
        Assert.Equal(receivedLimit, ReceivedFrameEvidence.MaxJournalBytes(
            Settings.CaptureBytes, Root.FlushBytes, ReceivedEvidenceKind.Json));
        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(() => BoundedJsonResponseReader.ReadRawAsync(
            response, Settings.CaptureBytes, Root.FlushBytes, deadline.Token, "evidence-block"));
        Assert.Equal("response_capture_limit", failure.ErrorCode);
        Assert.Equal(2, body.ReadStream.ReadCalls);
        Assert.Equal(0, body.ReadStream.EofReadCalls);
        Assert.Equal(receivedLimit, body.ReadStream.ReceivedBytes);
        AssertHttpEvidence(failure, bytes.AsSpan(0, receivedLimit).ToArray(), bodyRead: true, bodyComplete: false,
            null, Settings.CaptureBytes, Root.FlushBytes, deadline.Token);
        // Even when the last received byte happens to be the source's end, no zero read occurred.
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("invalid-utf8")]
    [InlineData("array")]
    [InlineData("null")]
    [InlineData("number")]
    [InlineData("normalized-limit")]
    [InlineData("valid-object")]
    public async Task HttpCompletedBodiesRetainExactRawEvidenceForParseObjectAndNormalizedFailures(string row)
    {
        using var deadline = Settings.Deadline();
        var bytes = JsonRow(row);
        Assert.InRange(bytes.Length, 0, Settings.CaptureBytes);
        using var body = new TrackingContent(bytes, [Root.FlushBytes]);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        var owned = await BoundedJsonResponseReader.ReadRawAsync(
            response, Settings.CaptureBytes, Root.FlushBytes, deadline.Token, "evidence-json");
        Assert.Equal(bytes, owned.ToArray());
        Assert.Equal(1, body.ReadStream.EofReadCalls);
        Dictionary<string, object?>? parsedObject = null;
        var error = Record.Exception(() =>
        {
            var parsed = BoundedJsonResponseReader.Parse(
                response, owned, Settings.CaptureBytes, Root.FlushBytes, deadline.Token, "evidence-json");
            parsedObject = BoundedJsonResponseReader.RequireObject(
                parsed, response, owned, Settings.CaptureBytes, Root.FlushBytes, "evidence-json");
        });
        if (row == "valid-object")
        {
            Assert.Null(error);
            Assert.Equal(true, parsedObject!["ok"]);
            Assert.False(ReceivedFrameEvidence.IsEvidence(parsedObject));
            return;
        }
        var failure = Assert.IsType<CoreUpstreamException>(error);
        Assert.Equal(row == "normalized-limit" ? "response_capture_limit" : "invalid_upstream_json", failure.ErrorCode);
        AssertHttpEvidence(failure, bytes, bodyRead: true, bodyComplete: true,
            null, Settings.CaptureBytes, Root.FlushBytes, deadline.Token);
    }

    private static byte[] JsonRow(string row)
    {
        if (row == "normalized-limit")
        {
            const string prefix = "{\"n\":1e20,\"padding\":\"";
            const string suffix = "\"}";
            var padding = new string('x', Settings.CaptureBytes - Encoding.UTF8.GetByteCount(prefix + suffix));
            var bytes = Encoding.UTF8.GetBytes(prefix + padding + suffix);
            Assert.Equal(Settings.CaptureBytes, bytes.Length);
            var normalized = new Dictionary<string, object?> { ["n"] = 1e20, ["padding"] = padding };
            Assert.Equal(Settings.CaptureBytes + 1L, ProtocolJsonWriter.Count(normalized, audit: true, CancellationToken.None));
            return bytes;
        }
        return row switch
        {
            "empty" => [],
            "invalid" => Encoding.UTF8.GetBytes("{\"unclosed\":"),
            "invalid-utf8" => [0xFF],
            "array" => Encoding.UTF8.GetBytes("[]"),
            "null" => Encoding.UTF8.GetBytes("null"),
            "number" => Encoding.UTF8.GetBytes("42"),
            "valid-object" => Encoding.UTF8.GetBytes("{\"ok\":true}"),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
    }

    private static SseFrame GeneratedFrame(int targetBytes, bool includeUsage = false)
    {
        var payload = new Dictionary<string, object?> { ["type"] = "ping", ["padding"] = "中😀" };
        if (includeUsage)
            payload["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 7, ["output_tokens"] = 3 };
        var fixedBytes = SseFrame.FromJson(payload).Utf8Length;
        Assert.True(targetBytes >= fixedBytes);
        payload["padding"] = new string('x', targetBytes - fixedBytes) + "中😀";
        var frame = SseFrame.FromJson(payload);
        Assert.Equal(targetBytes, frame.Utf8Length);
        return frame;
    }

    private static SseFrame[] ToolFrames(string partial) =>
    [
        SseFrame.FromJson(new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = "msg-evidence", ["type"] = "message", ["role"] = "assistant",
                ["content"] = new List<object?>(),
                ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 7 }
            }
        }),
        SseFrame.FromJson(new Dictionary<string, object?>
        {
            ["type"] = "content_block_start", ["index"] = 0,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "tool_use", ["id"] = "call-evidence", ["name"] = "fixture_tool",
                ["input"] = new Dictionary<string, object?> { ["original"] = true }
            }
        }),
        SseFrame.FromJson(new Dictionary<string, object?>
        {
            ["type"] = "content_block_delta", ["index"] = 0,
            ["delta"] = new Dictionary<string, object?> { ["type"] = "input_json_delta", ["partial_json"] = partial }
        }),
        SseFrame.FromJson(new Dictionary<string, object?> { ["type"] = "message_stop" })
    ];

    private static void AssertActualFrame(SseFrame frame, int sourceMargin)
    {
        Assert.True(frame.BoundaryComplete);
        Assert.False(frame.Malformed);
        Assert.Equal(Encoding.UTF8.GetByteCount(frame.WireText), frame.Utf8Length);
        Assert.InRange(frame.Utf8Length, 1, sourceMargin);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % (byte.MaxValue + 1));
        return bytes;
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

    private static byte[] Decode(JsonElement envelope) =>
        Convert.FromBase64String(envelope.GetProperty("received_utf8_base64").GetString()!);

    private static void AssertJournal(byte[] journal, params SseFrame[] frames)
    {
        var offset = 0;
        foreach (var frame in frames)
        {
            Assert.InRange(offset + sizeof(long), sizeof(long), journal.Length);
            var length = BinaryPrimitives.ReadInt64LittleEndian(journal.AsSpan(offset, sizeof(long)));
            Assert.Equal(frame.Utf8Length, length);
            offset += sizeof(long);
            var expected = Encoding.UTF8.GetBytes(frame.WireText);
            Assert.Equal(expected, journal.AsSpan(offset, checked((int)length)).ToArray());
            offset += checked((int)length);
        }
        Assert.Equal(journal.Length, offset);
    }

    private static void AssertMetadata(JsonElement envelope, string representation, long records,
        long receivedBytes, long journalBytes, int captureBytes, int marginBytes, bool prefixExceeded)
    {
        var metadata = envelope.GetProperty("_opencodex_capture");
        Assert.False(metadata.GetProperty("completed").GetBoolean());
        Assert.False(metadata.GetProperty("truncated").GetBoolean());
        Assert.False(metadata.GetProperty("source_reconstructed").GetBoolean());
        Assert.Equal(representation, metadata.GetProperty("representation").GetString());
        Assert.Equal("base64", metadata.GetProperty("encoding").GetString());
        Assert.Equal(representation == "received_json" ? "raw_utf8" : "int64_le_length_prefixed_utf8",
            metadata.GetProperty("framing").GetString());
        Assert.Equal(records, metadata.GetProperty("record_count").GetInt64());
        Assert.Equal(receivedBytes, metadata.GetProperty("received_bytes").GetInt64());
        Assert.Equal(journalBytes, metadata.GetProperty("journal_bytes").GetInt64());
        Assert.Equal(captureBytes, metadata.GetProperty("prefix_limit_bytes").GetInt32());
        Assert.Equal(marginBytes, metadata.GetProperty("overflow_margin_bytes").GetInt32());
        Assert.Equal(representation == "received_json" ? 0 : sizeof(long), metadata.GetProperty("length_header_bytes").GetInt32());
        Assert.Equal(prefixExceeded, metadata.GetProperty("prefix_limit_exceeded").GetBoolean());
    }

    private static void AssertHttpEvidence(CoreUpstreamException failure, byte[] raw, bool bodyRead,
        bool bodyComplete, long? declaredLength, int captureBytes, int blockBytes, CancellationToken token)
    {
        Assert.Equal(502, failure.StatusCode);
        var body = Assert.IsType<Dictionary<string, object?>>(failure.Body);
        Assert.True(UpstreamJsonEvidence.IsEvidence(body));
        Assert.True(ReceivedFrameEvidence.IsEvidence(body));
        var json = WriteJson(body, token);
        Assert.Equal(json.LongLength, ProtocolJsonWriter.Count(body, audit: true, token));
        Assert.Equal(UpstreamJsonEvidence.MaxJsonBytes(captureBytes, blockBytes),
            BoundedJsonResponseReader.MaxJsonBytes(captureBytes, blockBytes));
        Assert.InRange(json.LongLength, 1L, BoundedJsonResponseReader.MaxJsonBytes(captureBytes, blockBytes));
        using var document = JsonDocument.Parse(json);
        var envelope = document.RootElement;
        Assert.Equal("upstream_json_body_evidence", envelope.GetProperty("type").GetString());
        Assert.Equal(bodyRead, envelope.GetProperty("body_read").GetBoolean());
        Assert.Equal(bodyComplete, envelope.GetProperty("body_complete").GetBoolean());
        Assert.Equal(200, envelope.GetProperty("upstream_status_code").GetInt32());
        var declared = envelope.GetProperty("declared_content_length");
        if (declaredLength is null) Assert.Equal(JsonValueKind.Null, declared.ValueKind);
        else Assert.Equal(declaredLength.Value, declared.GetInt64());
        var received = envelope.GetProperty("received_json");
        Assert.Equal(raw, Decode(received));
        AssertMetadata(received, "received_json", 1, raw.LongLength, raw.LongLength,
            captureBytes, blockBytes, raw.Length > captureBytes);
        Assert.False(received.GetProperty("_opencodex_capture").GetProperty("usage_complete").GetBoolean());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _holders = [];
        private readonly CancellationTokenSource _deadline = Settings.Deadline();
        private readonly string _temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public Fixture(int sourceMargin)
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(_temporaryParent, "ocxp-received-evidence-" + Guid.NewGuid().ToString("N")));
            Spool = new ProxyAuditSpool(new ProxyAuditSpoolOptions
            {
                Directory = DirectoryPath, MemoryThresholdBytes = 0, OrphanAgeSeconds = Settings.OrphanAgeSeconds
            });
            Store = new SnapshotStore(Spool);
            try
            {
                Assert.Equal(Root.FlushBytes, Store.PageBytes);
                Reservation = Store.Reserve(Token);
            }
            catch
            {
                Spool.Dispose();
                _deadline.Dispose();
                Assert.Equal((0L, 0L, 0, 0), Store.Usage);
                DeleteOwnDirectory();
                throw;
            }
        }
        public string DirectoryPath { get; }
        public ProxyAuditSpool Spool { get; }
        public SnapshotStore Store { get; }
        public SnapshotReservation Reservation { get; }
        public CancellationToken Token => _deadline.Token;
        public T Hold<T>(T value) where T : IDisposable { _holders.Add(value); return value; }
        public StreamResponseCapture Capture(string protocol, int captureBytes, int sourceMargin)
        {
            var budget = new StreamCaptureBudget(captureBytes, Root.SseMaxCaptureItems,
                Root.SseMaxCaptureFields, Reservation.AuditCapture, Token);
            try { return Hold(new StreamResponseCapture(protocol, budget, maxEventBytes: sourceMargin)); }
            catch { budget.Dispose(); throw; }
        }
        public void Dispose()
        {
            for (var index = _holders.Count - 1; index >= 0; index--) _holders[index].Dispose();
            _holders.Clear();
            Reservation.Dispose();
            Spool.Dispose();
            _deadline.Dispose();
            Assert.Equal((0L, 0L, 0, 0), Store.Usage);
            DeleteOwnDirectory();
        }
        private void DeleteOwnDirectory()
        {
            Assert.Equal(_temporaryParent, Path.GetDirectoryName(DirectoryPath));
            Assert.StartsWith("ocxp-received-evidence-", Path.GetFileName(DirectoryPath));
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly bool _forbidRead;
        public TrackingContent(byte[] bytes, int[] chunks, bool forbidRead = false)
        {
            ReadStream = new TrackingReadStream(bytes, chunks);
            _forbidRead = forbidRead;
        }
        public TrackingReadStream ReadStream { get; }
        public int OpenReadCalls { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new Xunit.Sdk.XunitException("The bounded ingress must not buffer or serialize an upstream body.");
        protected override Task<Stream> CreateContentReadStreamAsync() => OpenRead(CancellationToken.None);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => OpenRead(cancellationToken);
        private Task<Stream> OpenRead(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OpenReadCalls++;
            if (_forbidRead) throw new Xunit.Sdk.XunitException("Oversized Content-Length must reject before body access.");
            return Task.FromResult<Stream>(ReadStream);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) ReadStream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingReadStream(byte[] bytes, int[] chunks) : Stream
    {
        private int _position, _chunk;
        private bool _disposed;
        public int ReadCalls { get; private set; }
        public int EofReadCalls { get; private set; }
        public int ReceivedBytes => _position;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReadCalls++;
            if (_position == bytes.Length) { EofReadCalls++; return 0; }
            var offered = _chunk < chunks.Length ? chunks[_chunk++] : destination.Length;
            var count = Math.Min(Math.Min(offered, destination.Length), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }

    private sealed class OneResponseHandler(TrackingContent content) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed record TestParameters(int CaptureBytes, int EventBytes, int CaseTimeoutMilliseconds,
        int RepeatCount, int OrphanAgeSeconds)
    {
        public int WebSocketMessageBytes => checked((int)((long)EventBytes
            * ProtocolConversionParameters.Current.SseMaxWebSocketMessageBytes / ProtocolConversionParameters.Current.SseMaxEventBytes));
        public int TimeoutSeconds => checked((int)((CaseTimeoutMilliseconds + 999L) / 1000L));
        public CancellationTokenSource Deadline() => new(CaseTimeoutMilliseconds);
        public static TestParameters Load() => new(
            WsHistoryTestParameters.OutputCaptureMaxBytes,
            WsHistoryTestParameters.OutputEventMaxBytes,
            WsHistoryTestParameters.CaseTimeoutMilliseconds,
            WsHistoryTestParameters.OutputItemCount,
            WsHistoryTestParameters.SnapshotOrphanAgeSeconds);
    }
}
