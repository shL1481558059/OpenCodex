using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using OpenCodex.ProtocolConversion.Abstractions;
using OpenCodex.ProtocolConversion.Errors;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Streaming;

namespace OpenCodex.ProtocolConversion;

internal enum ReceivedEvidenceKind { Frames, Chunks, Json }

/// <summary>
/// One charged append buffer, not a per-frame offsets list. C bounds the received prefix
/// including length headers; one complete frame accepted by its upstream reader may cross
/// that boundary. Its explicit source limit is SSE E or Responses WS M, not a secret max.
/// Rejected/incomplete network messages are not claimed to be complete captured frames.
/// Evidence is not a claim that a canonical source response was reconstructed.
/// </summary>
internal sealed class ReceivedFrameEvidence : IDisposable
{
    private const string MixedFrameWireFormat = "mixed_sse_frame_and_responses_websocket_text_json_utf8";
    private readonly IProtocolAuditCapture _capture;
    private readonly CancellationToken _cancellation;
    private readonly string _protocol;
    private readonly int _prefixBytes, _frameBytes;
    private IProtocolAuditBuffer? _journal;
    private ReceivedEvidenceKind _kind;
    private string? _frameWireFormat;
    private bool _kindChosen, _prefixExceeded, _sealed, _disposed;
    private ExceptionDispatchInfo? _recordingFailure;
    private long _records, _receivedBytes;
    private Dictionary<string, object?>? _usage;
    private readonly Dictionary<string, long> _usagePropertyBytes = new(StringComparer.Ordinal);
    private long _usageBytes = 2;
    private bool _usageComplete = true;

    internal ReceivedFrameEvidence(string protocol, int prefixBytes, int frameBytes,
        IProtocolAuditCapture capture, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefixBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        ArgumentNullException.ThrowIfNull(capture);
        _protocol = protocol; _prefixBytes = prefixBytes; _frameBytes = frameBytes;
        _capture = capture; _cancellation = cancellationToken;
    }

    internal long RecordCount => _records;

    public static long MaxJournalBytes(int prefixBytes, int overflowBytes,
        ReceivedEvidenceKind kind = ReceivedEvidenceKind.Frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefixBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overflowBytes);
        ValidateKind(kind);
        return checked((long)prefixBytes + overflowBytes + (kind == ReceivedEvidenceKind.Json ? 0 : sizeof(long)));
    }

    /// <summary>
    /// Bound for this exact relaxed-audit JSON envelope: fixed metadata with largest legal
    /// counters, the longest legal wire-format/boundary metadata, RFC 4648 base64 of the
    /// journal, and at most the explicit source margin (SSE E or WS M) of genuine usage.
    /// JSON-body evidence has no frame header and never invents parsed usage.
    /// </summary>
    public static long MaxJsonBytes(int prefixBytes, int overflowBytes,
        ReceivedEvidenceKind kind = ReceivedEvidenceKind.Frames)
    {
        var journalBytes = MaxJournalBytes(prefixBytes, overflowBytes, kind);
        var records = kind == ReceivedEvidenceKind.Json ? 1 : journalBytes / sizeof(long);
        var usage = kind == ReceivedEvidenceKind.Json ? null : new Dictionary<string, object?>();
        long shell = 0;
        foreach (var termination in Enum.GetValues<StreamCaptureTermination>())
        {
            // False is the longer boolean spelling. Empty base64 already includes its quotes.
            var envelope = CreateEnvelope(kind, termination, records, journalBytes, journalBytes,
                prefixBytes, overflowBytes, prefixExceeded: false, usageComplete: false, "", usage,
                observerFailed: false, wireFormat: kind == ReceivedEvidenceKind.Frames ? MixedFrameWireFormat : null);
            shell = Math.Max(shell, ProtocolJsonWriter.Count(envelope, audit: true, CancellationToken.None));
        }
        var base64Bytes = checked(4L * ((journalBytes + 2) / 3));
        var usageBytes = kind == ReceivedEvidenceKind.Json ? 0 : Math.Max(0L, overflowBytes - 2L);
        return checked(shell + base64Bytes + usageBytes);
    }

    internal void Append(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        AppendRecord(frame, ReceivedEvidenceKind.Frames);
    }

    internal void AppendChunk(string chunk) =>
        AppendRecord(new SseFrame(chunk, null), ReceivedEvidenceKind.Chunks);

    private void AppendRecord(SseFrame frame, ReceivedEvidenceKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recordingFailure?.Throw();
        if (_sealed || _prefixExceeded)
            throw new InvalidOperationException("Received-frame evidence is already complete.");
        _cancellation.ThrowIfCancellationRequested();
        if (_kindChosen && _kind != kind)
            throw new InvalidOperationException("Frame and chunk evidence cannot share one journal.");
        _kind = kind; _kindChosen = true;
        try
        {
            // The transport may expose semantic SSE whose default escaping/wrapper is larger
            // than the accepted WS message. M measures original source bytes, never that adapter.
            var frameBytes = frame.EvidenceUtf8Length;
            if (frameBytes > _frameBytes)
                throw new UpstreamException("Complete stream audit exceeds its accepted-source frame byte budget; no frame was truncated.",
                    "evidence_capture_limit", 507);
            _journal ??= _capture.CreateStaging(_cancellation);
            Span<byte> header = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(header, frameBytes);
            _journal.Write(header);
            using (var output = new AuditBufferWriter(_journal))
            {
                frame.WriteEvidenceUtf8(output);
                if (output.WrittenBytes != frameBytes)
                    throw new InvalidDataException("Received frame changed while its exact evidence was written.");
            }
            _records = checked(_records + 1);
            _receivedBytes = checked(_receivedBytes + frameBytes);
            var wireFormat = frame.EvidenceWireFormat;
            _frameWireFormat = _frameWireFormat is null || _frameWireFormat == wireFormat
                ? wireFormat : MixedFrameWireFormat;
        }
        catch (Exception exception)
        {
            // Never publish a partially written record as complete evidence, or retry its write.
            _recordingFailure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
        CaptureUsage(frame.Event?.Data);
        if (_journal!.Length > _prefixBytes)
        {
            _prefixExceeded = true;
            throw new UpstreamException("Complete stream audit exceeds its received-frame evidence prefix budget; the complete offending frame was retained.",
                "evidence_capture_limit", 507);
        }
    }

    internal ReceivedFrameEvidenceSnapshot Snapshot(StreamCaptureTermination termination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recordingFailure?.Throw();
        _sealed = true;
        var reader = _journal?.OpenSnapshotRead() ?? Stream.Null;
        try
        {
            var bytes = _journal?.Length ?? 0;
            if (reader.Length != bytes)
                throw new InvalidDataException("Received-frame evidence snapshot length changed.");
            var value = new ReaderBase64Value(reader, bytes);
            var envelope = CreateEnvelope(_kindChosen ? _kind : ReceivedEvidenceKind.Frames,
                termination, _records, _receivedBytes, bytes, _prefixBytes, _frameBytes,
                _prefixExceeded, _usageComplete, value,
                _usage is null ? null : StreamCaptureValues.CloneObject(_usage), wireFormat: _frameWireFormat);
            return new ReceivedFrameEvidenceSnapshot(envelope, value);
        }
        catch { reader.Dispose(); throw; }
    }

    /// <summary>Adopts immutable caller-owned bytes. No copy, giant base64 string, or private store.</summary>
    public static Dictionary<string, object?> CreateJsonEvidence(ReadOnlyMemory<byte> ownedUtf8,
        int prefixBytes, int overflowBlockBytes)
    {
        if (ownedUtf8.Length > MaxJournalBytes(prefixBytes, overflowBlockBytes, ReceivedEvidenceKind.Json))
            throw new ArgumentException("JSON evidence exceeds its declared received-prefix bound.", nameof(ownedUtf8));
        return CreateEnvelope(ReceivedEvidenceKind.Json, StreamCaptureTermination.UpstreamError,
            1, ownedUtf8.Length, ownedUtf8.Length, prefixBytes, overflowBlockBytes,
            ownedUtf8.Length > prefixBytes, usageComplete: false, new MemoryBase64Value(ownedUtf8), null);
    }

    public static bool IsEvidence(object? value)
    {
        if (HasOwnedEnvelope(value)) return true;
        if (value is not IReadOnlyDictionary<string, object?> response) return false;
        // Only the known Core adapter/error wrappers are unwrapped, without recursive traversal.
        if (response.TryGetValue("received_json", out var nested) && HasOwnedEnvelope(nested)) return true;
        if (!response.TryGetValue("error", out var error)) return false;
        return HasOwnedEnvelope(error)
            || error is IReadOnlyDictionary<string, object?> errorObject
            && errorObject.TryGetValue("received_json", out nested) && HasOwnedEnvelope(nested);
    }

    private static bool HasOwnedEnvelope(object? value) =>
        value is IReadOnlyDictionary<string, object?> response
        // An upstream JSON object can spoof metadata strings, but cannot create these private values.
        && response.TryGetValue("received_utf8_base64", out var encoded)
        && encoded is MemoryBase64Value or ReaderBase64Value
        && response.TryGetValue("_opencodex_capture", out var metadata)
        && metadata is IReadOnlyDictionary<string, object?> capture
        && capture.TryGetValue("representation", out var representation)
        && representation is "received_frames" or "received_chunks" or "received_json";

    private static void ValidateKind(ReceivedEvidenceKind kind)
    {
        if (kind is not (ReceivedEvidenceKind.Frames or ReceivedEvidenceKind.Chunks or ReceivedEvidenceKind.Json))
            throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static Dictionary<string, object?> CreateEnvelope(ReceivedEvidenceKind kind,
        StreamCaptureTermination termination, long records, long receivedBytes, long journalBytes,
        int prefixBytes, int marginBytes, bool prefixExceeded, bool usageComplete, object base64,
        Dictionary<string, object?>? usage, bool observerFailed = true, string? wireFormat = null)
    {
        ValidateKind(kind);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["completed"] = false,
            ["termination"] = termination.ToString(),
            ["truncated"] = false,
            ["observer_failed"] = observerFailed,
            ["source_reconstructed"] = false,
            ["representation"] = kind switch
            {
                ReceivedEvidenceKind.Frames => "received_frames",
                ReceivedEvidenceKind.Chunks => "received_chunks",
                _ => "received_json"
            },
            ["encoding"] = "base64",
            ["framing"] = kind == ReceivedEvidenceKind.Json ? "raw_utf8" : "int64_le_length_prefixed_utf8",
            ["wire_format"] = kind switch
            {
                ReceivedEvidenceKind.Frames => wireFormat ?? "no_accepted_frames",
                ReceivedEvidenceKind.Chunks => "utf8_chunks",
                _ => "json_utf8"
            },
            ["evidence_boundary"] = kind switch
            {
                // The complete WS message was EOM/M/object-admitted before delivery. This is
                // not a claim to contain rejected/oversized/incomplete network fragments.
                ReceivedEvidenceKind.Frames => "parser_delivered_accepted_frames",
                ReceivedEvidenceKind.Chunks => "caller_delivered_chunks",
                _ => "bounded_reader_received_prefix"
            },
            ["record_count"] = records,
            ["received_bytes"] = receivedBytes,
            ["journal_bytes"] = journalBytes,
            ["prefix_limit_bytes"] = prefixBytes,
            ["overflow_margin_bytes"] = marginBytes,
            ["length_header_bytes"] = kind == ReceivedEvidenceKind.Json ? 0 : sizeof(long),
            ["prefix_limit_exceeded"] = prefixExceeded,
            ["usage_complete"] = usageComplete
        };
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_opencodex_capture"] = metadata,
            ["received_utf8_base64"] = base64
        };
        if (usage is not null) result["usage"] = usage;
        return result;
    }

    private void CaptureUsage(object? data)
    {
        if (!_usageComplete || data is not IReadOnlyDictionary<string, object?> payload) return;
        IReadOnlyDictionary<string, object?>? usage = null;
        if (payload.TryGetValue("response", out var response)
            && response is IReadOnlyDictionary<string, object?> responseObject
            && responseObject.TryGetValue("usage", out var responseUsage))
            usage = responseUsage as IReadOnlyDictionary<string, object?>;
        if (usage is null && payload.TryGetValue("message", out var message)
            && message is IReadOnlyDictionary<string, object?> messageObject
            && messageObject.TryGetValue("usage", out var messageUsage))
            usage = messageUsage as IReadOnlyDictionary<string, object?>;
        if (usage is null && payload.TryGetValue("usage", out var directUsage))
            usage = directUsage as IReadOnlyDictionary<string, object?>;
        if (usage is null) return;

        var merge = _protocol == ProtocolConverter.Messages;
        var bytes = merge ? _usageBytes : 2L;
        var count = merge ? _usagePropertyBytes.Count : 0;
        var updates = new List<(string Key, object? Value, long Bytes)>();
        foreach (var (key, value) in usage)
        {
            // Count only this incoming property, never a growing accumulated source object.
            var property = new Dictionary<string, object?> { [key] = value };
            var propertyBytes = checked(ProtocolJsonWriter.Count(property, audit: true, _cancellation) - 2);
            if (merge && _usagePropertyBytes.TryGetValue(key, out var previous)) bytes -= previous;
            else { if (count > 0) bytes++; count++; }
            bytes = checked(bytes + propertyBytes);
            updates.Add((key, value, propertyBytes));
        }
        if (bytes > _frameBytes)
        {
            // The complete usage remains in the exact journal. Keep only the genuine previous
            // snapshot and explicitly mark it partial; never manufacture complete usage.
            _usageComplete = false;
            return;
        }
        if (!merge) { _usage = null; _usagePropertyBytes.Clear(); }
        _usage ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value, propertyBytes) in updates)
        {
            _usage[key] = StreamCaptureValues.CloneValue(value);
            _usagePropertyBytes[key] = propertyBytes;
        }
        _usageBytes = bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _journal?.Dispose(); _journal = null;
        // Snapshot readers are independent references, owned by ReceivedFrameEvidenceSnapshot.
    }

    private sealed class AuditBufferWriter(IProtocolAuditBuffer target) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(ProtocolConversionParameters.Current.FlushBytes);
        internal long WrittenBytes { get; private set; }
        public void Advance(int count)
        {
            if ((uint)count > (uint)_buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            target.Write(_buffer.AsSpan(0, count));
            WrittenBytes = checked(WrittenBytes + count);
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint > _buffer.Length)
            {
                var next = ArrayPool<byte>.Shared.Rent(sizeHint);
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true); _buffer = next;
            }
            return _buffer;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    }

    private sealed class MemoryBase64Value(ReadOnlyMemory<byte> owned) : IProtocolJsonValue
    {
        public void Write(Utf8JsonWriter writer, CancellationToken cancellationToken)
        {
            using var source = new MemoryReader(owned);
            WriteBase64(writer, source, owned.Length, cancellationToken);
        }
    }

    internal sealed class ReaderBase64Value(Stream reader, long length) : IProtocolJsonValue, IDisposable
    {
        private readonly object _gate = new();
        private Stream? _reader = reader;
        public void Write(Utf8JsonWriter writer, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var source = _reader ?? throw new ObjectDisposedException(nameof(ReaderBase64Value));
                source.Position = 0;
                WriteBase64(writer, source, length, cancellationToken);
            }
        }
        public void Dispose() { lock (_gate) { _reader?.Dispose(); _reader = null; } }
    }

    private static void WriteBase64(Utf8JsonWriter writer, Stream source, long length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length == 0) { writer.WriteStringValue(""); return; }
        // Three-byte quanta are a base64 format requirement, not a capacity default.
        var capacity = Math.Max(3, ProtocolConversionParameters.Current.FlushBytes);
        var bytes = ArrayPool<byte>.Shared.Rent(capacity);
        char[]? chars = null;
        try
        {
            chars = ArrayPool<char>.Shared.Rent(checked(4 * ((capacity + 2) / 3)));
            var remaining = length;
            var carry = 0;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(bytes.AsSpan(carry, checked((int)Math.Min(capacity - carry, remaining))));
                if (read == 0) throw new EndOfStreamException("Received evidence reader ended before its sealed length.");
                remaining -= read;
                var total = carry + read;
                var final = remaining == 0;
                var encode = final ? total : total / 3 * 3;
                if (encode > 0)
                {
                    if (!Convert.TryToBase64Chars(bytes.AsSpan(0, encode), chars, out var written))
                        throw new InvalidOperationException("The bounded base64 output buffer is too small.");
                    writer.WriteStringValueSegment(chars.AsSpan(0, written), final);
                    writer.Flush();
                }
                carry = total - encode;
                if (carry > 0) bytes.AsSpan(encode, carry).CopyTo(bytes);
            }
        }
        finally
        {
            if (chars is not null) ArrayPool<char>.Shared.Return(chars, clearArray: true);
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
        }
    }

    private sealed class MemoryReader(ReadOnlyMemory<byte> memory) : Stream
    {
        private long _position;
        public override int Read(Span<byte> destination)
        {
            var count = checked((int)Math.Min(destination.Length, Math.Max(0, memory.Length - _position)));
            memory.Span.Slice(checked((int)_position), count).CopyTo(destination);
            _position += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(memory.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0) throw new IOException("Cannot seek before owned evidence.");
            return _position = next;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => memory.Length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Owns an independent journal read reference until complete audit persistence finishes.</summary>
internal sealed class ReceivedFrameEvidenceSnapshot : IDisposable
{
    private readonly IDisposable _reader;
    internal ReceivedFrameEvidenceSnapshot(Dictionary<string, object?> response, IDisposable reader)
    { Response = response; _reader = reader; }
    public Dictionary<string, object?> Response { get; }
    public void Dispose() => _reader.Dispose();
}
