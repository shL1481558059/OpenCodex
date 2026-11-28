using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Json;
using Xunit;
using Xunit.Abstractions;
using CoreUpstreamException = OpenCodex.Core.Errors.UpstreamException;

namespace OpenCodex.Api.Tests;

public sealed class StreamingHttpErrorEvidenceRegressionTests
{
    private static readonly ProtocolConversionParameters Root = ProtocolConversionParameters.Current;
    private static readonly CaseParameters Cases = CaseParameters.Load();

    public StreamingHttpErrorEvidenceRegressionTests(ITestOutputHelper output)
        => output.WriteLine("ActualRoot P={0} C={1} F={2}; CaseTimeoutMilliseconds={3}; no Root C override",
            Root.MaxRequestBodyBytes, Root.SseMaxCaptureBytes, Root.FlushBytes, Cases.TimeoutMilliseconds);

    [Fact]
    public async Task OversizedDeclaredErrorBodyIsRejectedUnreadBeforeAnyRetry()
    {
        using var deadline = Cases.Deadline();
        var contents = new List<StreamingOnlyJsonContent>();
        var declared = checked((long)Root.SseMaxCaptureBytes + 1);
        using var handler = new ResponseHandler(HttpStatusCode.TooManyRequests, () =>
        {
            var content = new StreamingOnlyJsonContent(new SyntheticJsonStream(0, validJson: true), declared);
            contents.Add(content);
            return content;
        });
        using var http = new HttpClient(handler);
        var upstream = new HttpUpstreamClient(http, (_, _) => Task.CompletedTask);
        await using var events = upstream.StreamEventsAsync(Channel(Cases.RetryAttempts), Payload(),
            Cases.TimeoutSeconds, deadline.Token).GetAsyncEnumerator(deadline.Token);

        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(async () => { await events.MoveNextAsync(); });

        Assert.Equal(BoundedJsonResponseReader.CaptureLimitCode, failure.ErrorCode);
        Assert.Equal(1, handler.Calls);
        var only = Assert.Single(contents);
        Assert.Equal(0, only.StreamOpenCalls);
        Assert.Equal(0, only.WholeBodySerializationCalls);
        Assert.Equal(0L, only.Source.ReadBytes);
        var envelope = Envelope(failure, only.Source, bodyRead: false, bodyComplete: false, declared,
            HttpStatusCode.TooManyRequests, deadline.Token);
        Assert.False((bool)Capture(envelope)["prefix_limit_exceeded"]!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownLengthErrorPreservesWholeReceivedValidOrInvalidOverflowPrefix(bool validJson)
    {
        using var deadline = Cases.Deadline();
        // The source is virtual; only the production reader owns a root-sized raw buffer.
        var source = new SyntheticJsonStream(UpstreamJsonEvidence.MaxRawBytes(
            Root.SseMaxCaptureBytes, Root.FlushBytes), validJson);
        using var content = new StreamingOnlyJsonContent(source, declaredLength: null);
        using var handler = new ResponseHandler(HttpStatusCode.BadRequest, () => content);
        using var http = new HttpClient(handler);
        var upstream = new HttpUpstreamClient(http, (_, _) => Task.CompletedTask);
        await using var events = upstream.StreamEventsAsync(Channel(0), Payload(),
            Cases.TimeoutSeconds, deadline.Token).GetAsyncEnumerator(deadline.Token);

        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(async () => { await events.MoveNextAsync(); });

        Assert.Equal(BoundedJsonResponseReader.CaptureLimitCode, failure.ErrorCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, content.StreamOpenCalls);
        Assert.Equal(0, content.WholeBodySerializationCalls);
        Assert.InRange(source.ReadBytes, checked((long)Root.SseMaxCaptureBytes + 1),
            (long)UpstreamJsonEvidence.MaxRawBytes(Root.SseMaxCaptureBytes, Root.FlushBytes));
        Assert.False(source.EndOfFileObserved);
        var envelope = Envelope(failure, source, bodyRead: true, bodyComplete: false,
            declaredLength: null, HttpStatusCode.BadRequest, deadline.Token);
        Assert.True((bool)Capture(envelope)["prefix_limit_exceeded"]!);
    }

    [Fact]
    public async Task InvalidUnknownLengthErrorWithinRootBudgetIsNotClippedOrInventedAsJson()
    {
        using var deadline = Cases.Deadline();
        // A published fixture length exceeds the old 2000-character clip while staying below real Root C.
        Assert.True(Cases.InvalidBodyBytes < Root.SseMaxCaptureBytes);
        var source = new SyntheticJsonStream(Cases.InvalidBodyBytes, validJson: false);
        using var content = new StreamingOnlyJsonContent(source, declaredLength: null);
        using var handler = new ResponseHandler(HttpStatusCode.BadRequest, () => content);
        using var http = new HttpClient(handler);
        var upstream = new HttpUpstreamClient(http, (_, _) => Task.CompletedTask);
        await using var events = upstream.StreamEventsAsync(Channel(0), Payload(),
            Cases.TimeoutSeconds, deadline.Token).GetAsyncEnumerator(deadline.Token);

        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(async () => { await events.MoveNextAsync(); });

        Assert.Equal(BoundedJsonResponseReader.InvalidJsonCode, failure.ErrorCode);
        Assert.Equal((int)HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, content.WholeBodySerializationCalls);
        Assert.Equal((long)Cases.InvalidBodyBytes, source.ReadBytes);
        Assert.True(source.EndOfFileObserved);
        var envelope = Envelope(failure, source, bodyRead: true, bodyComplete: true,
            declaredLength: null, HttpStatusCode.BadRequest, deadline.Token);
        Assert.False((bool)Capture(envelope)["prefix_limit_exceeded"]!);
    }

    [Fact]
    public async Task SmallLegalProviderErrorKeepsItsOriginalHttpStatusAndJsonBody()
    {
        using var deadline = Cases.Deadline();
        using var handler = new ResponseHandler(HttpStatusCode.Unauthorized, () =>
            new StringContent("{\"error\":{\"message\":\"fixture provider error\"}}", Encoding.UTF8, "application/json"));
        using var http = new HttpClient(handler);
        var upstream = new HttpUpstreamClient(http, (_, _) => Task.CompletedTask);
        await using var events = upstream.StreamEventsAsync(Channel(0), Payload(),
            Cases.TimeoutSeconds, deadline.Token).GetAsyncEnumerator(deadline.Token);

        var failure = await Assert.ThrowsAsync<CoreUpstreamException>(async () => { await events.MoveNextAsync(); });

        Assert.Equal((int)HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Null(failure.ErrorCode);
        Assert.Equal(1, handler.Calls);
        var body = Assert.IsType<Dictionary<string, object?>>(failure.Body);
        var error = Assert.IsType<Dictionary<string, object?>>(body["error"]);
        Assert.Equal("fixture provider error", error["message"]);
        Assert.False(UpstreamJsonEvidence.IsEvidence(body));
    }

    private static Dictionary<string, object?> Envelope(CoreUpstreamException failure, SyntheticJsonStream source,
        bool bodyRead, bool bodyComplete, long? declaredLength, HttpStatusCode status, CancellationToken cancellationToken)
    {
        var envelope = Assert.IsType<Dictionary<string, object?>>(failure.Body);
        Assert.True(UpstreamJsonEvidence.IsEvidence(envelope));
        Assert.Equal("upstream_json_body_evidence", envelope["type"]);
        Assert.Equal(bodyRead, envelope["body_read"]);
        Assert.Equal(bodyComplete, envelope["body_complete"]);
        Assert.Equal(declaredLength, envelope["declared_content_length"]);
        Assert.Equal((int)status, envelope["upstream_status_code"]);
        var capture = Capture(envelope);
        Assert.Equal("received_json", capture["representation"]);
        Assert.Equal("raw_utf8", capture["framing"]);
        Assert.Equal(0, capture["length_header_bytes"]);
        Assert.Equal(source.ReadBytes, capture["received_bytes"]);
        Assert.Equal(source.ReadBytes, capture["journal_bytes"]);
        Assert.Equal(Root.SseMaxCaptureBytes, capture["prefix_limit_bytes"]);
        Assert.Equal(Root.FlushBytes, capture["overflow_margin_bytes"]);
        Assert.Equal(false, capture["completed"]);
        Assert.Equal(false, capture["source_reconstructed"]);
        Assert.Equal(false, capture["truncated"]);
        var received = Assert.IsType<Dictionary<string, object?>>(envelope["received_json"]);
        var value = Assert.IsAssignableFrom<IProtocolJsonValue>(received["received_utf8_base64"]);
        Assert.False(received["received_utf8_base64"] is string);
        using var decoded = new Base64DigestStream(Root.FlushBytes);
        using (var writer = new Utf8JsonWriter(decoded)) value.Write(writer, cancellationToken);
        Assert.Equal(source.ReadBytes, decoded.DecodedBytes);
        Assert.Equal(source.ObservedDigest, decoded.Digest());
        Assert.InRange(ProtocolJsonWriter.Count(envelope, audit: true, cancellationToken), 1L,
            UpstreamJsonEvidence.MaxJsonBytes(Root.SseMaxCaptureBytes, Root.FlushBytes));
        return envelope;
    }

    private static Dictionary<string, object?> Capture(Dictionary<string, object?> envelope)
    {
        var received = Assert.IsType<Dictionary<string, object?>>(envelope["received_json"]);
        return Assert.IsType<Dictionary<string, object?>>(received["_opencodex_capture"]);
    }

    private static Dictionary<string, object?> Channel(int retries) => new(StringComparer.Ordinal)
    {
        ["id"] = "stream-error-evidence-fixture", ["type"] = "responses", ["baseurl"] = "http://model.invalid",
        ["retry_count"] = retries, ["timeout_seconds"] = Cases.TimeoutSeconds
    };
    private static Dictionary<string, object?> Payload() => new(StringComparer.Ordinal)
    { ["model"] = "fixture-model", ["input"] = "fixture", ["stream"] = true };

    private sealed record CaseParameters(int TimeoutMilliseconds, int RetryAttempts, int InvalidBodyBytes)
    {
        public int TimeoutSeconds => checked((TimeoutMilliseconds + 999) / 1000);
        public CancellationTokenSource Deadline() => new(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
        public static CaseParameters Load()
        {
            return new(WsHistoryTestParameters.CaseTimeoutMilliseconds,
                WsHistoryTestParameters.OutputItemCount, WsHistoryTestParameters.OutputDeltaCharacters);
        }
    }

    private sealed class ResponseHandler(HttpStatusCode status, Func<HttpContent> content) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content() });
        }
    }

    private sealed class StreamingOnlyJsonContent : HttpContent
    {
        private readonly long? _declaredLength;
        public SyntheticJsonStream Source { get; }
        public int StreamOpenCalls { get; private set; }
        public int WholeBodySerializationCalls { get; private set; }
        public StreamingOnlyJsonContent(SyntheticJsonStream source, long? declaredLength)
        {
            Source = source; _declaredLength = declaredLength;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (declaredLength is { } length) Headers.ContentLength = length;
        }
        protected override bool TryComputeLength(out long length)
        { length = _declaredLength ?? 0; return _declaredLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WholeBodySerializationCalls++;
            throw new InvalidOperationException("This fixture forbids whole-body buffering; use its owned streaming reader.");
        }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { StreamOpenCalls++; return Task.FromResult<Stream>(Source); }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return CreateContentReadStreamAsync(); }
        protected override void Dispose(bool disposing)
        { if (disposing) Source.Dispose(); base.Dispose(disposing); }
    }

    private sealed class SyntheticJsonStream(long length, bool validJson) : Stream
    {
        private static readonly byte[] ValidPrefix = Encoding.UTF8.GetBytes("{\"error\":\"");
        private static readonly byte[] InvalidPrefix = Encoding.UTF8.GetBytes("not-json:");
        private static readonly byte[] ValidSuffix = Encoding.UTF8.GetBytes("\"}");
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private byte[]? _digest;
        private long _position;
        private bool _disposed;
        public long ReadBytes => _position;
        public bool EndOfFileObserved { get; private set; }
        public byte[] ObservedDigest => _digest ?? _hash.GetCurrentHash();
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var count = checked((int)Math.Min(buffer.Length, length - _position));
            if (count == 0) { EndOfFileObserved = true; return 0; }
            var bytes = buffer[..count]; bytes.Fill((byte)'x');
            var prefix = validJson ? ValidPrefix : InvalidPrefix;
            if (_position < prefix.Length)
            {
                var copied = Math.Min(count, prefix.Length - checked((int)_position));
                prefix.AsSpan(checked((int)_position), copied).CopyTo(bytes);
            }
            var suffixStart = length - ValidSuffix.Length;
            if (validJson && _position + count > suffixStart)
            {
                var start = Math.Max(_position, suffixStart);
                var copied = checked((int)(_position + count - start));
                ValidSuffix.AsSpan(checked((int)(start - suffixStart)), copied).CopyTo(bytes[checked((int)(start - _position))..]);
            }
            _hash.AppendData(bytes); _position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _digest = _hash.GetHashAndReset(); _hash.Dispose(); _disposed = true; }
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Root F sizes the temporary work buffers. Four/three are RFC4648 encoding quanta, not capacity settings.
    private sealed class Base64DigestStream : Stream
    {
        private readonly byte[] _encoded, _decoded;
        private readonly int _blockCharacters;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _pending;
        private bool _started, _closed;
        private long _written;
        public long DecodedBytes { get; private set; }
        public Base64DigestStream(int workBytes)
        {
            _blockCharacters = Math.Max(4, workBytes / 4 * 4);
            _encoded = new byte[checked(_blockCharacters + 4)];
            _decoded = new byte[checked(_encoded.Length / 4 * 3)];
        }
        public byte[] Digest()
        {
            if (!_started || !_closed || _pending != 0) throw new InvalidDataException("Incomplete streamed base64 JSON value.");
            return _hash.GetCurrentHash();
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _written = checked(_written + buffer.Length);
            if (buffer.IsEmpty) return;
            if (_closed) throw new InvalidDataException("Trailing bytes after streamed base64 JSON value.");
            if (!_started)
            {
                if (buffer[0] != (byte)'"') throw new InvalidDataException("A base64 JSON string was required.");
                _started = true; buffer = buffer[1..];
            }
            var closes = !buffer.IsEmpty && buffer[^1] == (byte)'"';
            if (closes) buffer = buffer[..^1];
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(buffer.Length, _encoded.Length - _pending);
                buffer[..take].CopyTo(_encoded.AsSpan(_pending)); _pending += take; buffer = buffer[take..];
                if (_pending == _encoded.Length) Decode(final: false);
            }
            if (closes) { Decode(final: true); _closed = true; }
        }
        private void Decode(bool final)
        {
            var count = final ? _pending : _blockCharacters;
            if (count == 0) return;
            var status = Base64.DecodeFromUtf8(_encoded.AsSpan(0, count), _decoded, out var consumed, out var written, final);
            if (status != OperationStatus.Done || consumed != count)
                throw new InvalidDataException("Invalid streamed base64 JSON evidence.");
            _hash.AppendData(_decoded.AsSpan(0, written)); DecodedBytes += written;
            _encoded.AsSpan(count, _pending - count).CopyTo(_encoded); _pending -= count;
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
