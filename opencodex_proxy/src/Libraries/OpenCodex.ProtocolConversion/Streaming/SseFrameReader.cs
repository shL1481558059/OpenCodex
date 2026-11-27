using System.Runtime.CompilerServices;
using System.Text;

namespace OpenCodex.ProtocolConversion.Streaming;

public sealed class SseFrameOptions
{
    // A bounded event, not a bound on the response. Configurable by transport/writer callers.
    public int MaxEventBytes { get; init; } = ProtocolConversionParameters.Current.SseMaxEventBytes;
    public int MaxCaptureBytes { get; set; } = ProtocolConversionParameters.Current.SseMaxCaptureBytes;
    public int MaxCaptureItems { get; set; } = ProtocolConversionParameters.Current.SseMaxCaptureItems;
    public int MaxCaptureFields { get; set; } = ProtocolConversionParameters.Current.SseMaxCaptureFields;
    public long MaxBufferedRoundBytes { get; init; } = ProtocolConversionParameters.Current.SseMaxBufferedRoundBytes;
    public int MaxBufferedRoundFrames { get; init; } = ProtocolConversionParameters.Current.SseMaxBufferedRoundFrames;
}

public sealed class SseTransportOptions
{
    public int MaxEventBytes { get; init; } = ProtocolConversionParameters.Current.SseMaxEventBytes;
    public int MaxProbeBytes { get; init; } = ProtocolConversionParameters.Current.SseMaxProbeBytes;
    public int MaxProbeFrames { get; init; } = ProtocolConversionParameters.Current.SseMaxProbeFrames;
    public int MaxWebSocketMessageBytes { get; init; } = ProtocolConversionParameters.Current.SseMaxWebSocketMessageBytes;
    public void Validate()
    {
        if (MaxEventBytes <= 0 || MaxProbeBytes <= 0 || MaxProbeFrames <= 0 || MaxWebSocketMessageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SseTransportOptions));
    }
}

/// <summary>Incremental CR/LF/CRLF and multi-line data assembler. One JSON parse per frame.</summary>
public sealed class SseFrameReader
{
    private readonly int _limit;
    private readonly int _retainedCharacters = ProtocolConversionParameters.Current.SseRetainedBufferCharacters;
    private StringBuilder _wire = new();
    private bool _lineHasContent;
    private bool _afterCr;
    private bool _firstLine = true;
    private long _wireBytes;
    private bool _highSurrogate;
    public SseFrameReader(SseFrameOptions? options = null)
    {
        _limit = (options ?? new()).MaxEventBytes;
        if (_limit <= 0) throw new ArgumentOutOfRangeException(nameof(options));
    }

    // Feed takes arbitrary chunks, not implied physical lines. CRLF is canonicalized to LF on
    // the wire; SSE data and field semantics are unchanged. No JSON probing before the boundary.
    public IEnumerable<SseFrame> Feed(string chunk)
    {
        for (var offset = 0; offset < chunk.Length;)
        {
            var frame = Read(chunk.AsSpan(offset), out var consumed, offset == 0 ? chunk : null);
            offset += consumed;
            if (frame is not null) yield return frame;
        }
    }

    public void Feed(ReadOnlySpan<char> chunk, Queue<SseFrame> output)
    {
        while (!chunk.IsEmpty)
        {
            var frame = Read(chunk, out var consumed);
            chunk = chunk[consumed..];
            if (frame is not null) output.Enqueue(frame);
        }
    }

    private SseFrame? Read(ReadOnlySpan<char> chunk, out int consumed, string? ownedChunk = null)
    {
        if (_wire.Length == 0)
        {
            var skip = _afterCr && chunk[0] == '\n' ? 1 : 0;
            var available = chunk[skip..];
            var boundary = available.IndexOf("\n\n");
            var length = available.StartsWith("\n") ? 1 : boundary >= 0 ? boundary + 2 : 0;
            if (length > 0 && !available[..length].Contains('\r'))
            {
                if (Encoding.UTF8.GetByteCount(available[..length]) > _limit) throw TooLarge();
                consumed = skip + length;
                _afterCr = false;
                var wire = skip == 0 && length == chunk.Length && ownedChunk is not null
                    ? ownedChunk
                    : available[..length].ToString();
                return Take(wire, complete: true);
            }
        }
        var start = 0;
        for (var index = 0; index < chunk.Length; index++)
        {
            var ch = chunk[index];
            if (_afterCr && ch == '\n')
            {
                _afterCr = false;
                start = index + 1;
                continue;
            }
            _afterCr = ch == '\r';
            if (ch is '\r' or '\n')
            {
                CountByteBudget('\n');
                _wire.Append(chunk[start..index]);
                _wire.Append('\n');
                start = index + 1;
                if (!_lineHasContent)
                {
                    consumed = start;
                    return Take(complete: true);
                }
                _lineHasContent = false;
            }
            else
            {
                CountByteBudget(ch);
                _lineHasContent = true;
            }
        }
        _wire.Append(chunk[start..]);
        consumed = chunk.Length;
        return null;
    }

    private void CountByteBudget(char ch)
    {
        // UTF8 replacement fallback uses 3 bytes for an unpaired surrogate. A following low
        // surrogate changes that pair to 4 bytes total, even across input chunk boundaries.
        _wireBytes += _highSurrogate && char.IsLowSurrogate(ch) ? 1 : ch <= 0x7f ? 1 : ch <= 0x7ff ? 2 : 3;
        _highSurrogate = char.IsHighSurrogate(ch);
        if (_wireBytes > _limit) throw TooLarge();
    }

    public SseFrame? Complete()
    {
        if (_wire.Length == 0) return null;
        return Take(_wire.ToString(), complete: false);
    }

    private SseFrame Take(bool complete) => Take(_wire.ToString(), complete);

    private SseFrame Take(string wire, bool complete)
    {
        ReadOnlyMemory<char>? name = null;
        ReadOnlyMemory<char>? data = null;
        StringBuilder? multilineData = null;

        for (var offset = 0; offset < wire.Length;)
        {
            var length = wire.AsSpan(offset).IndexOf('\n');
            if (length < 0) length = wire.Length - offset;
            var line = wire.AsMemory(offset, length);
            offset += length + 1;
            if (line.IsEmpty) continue;
            if (_firstLine)
            {
                var trimmedLength = line.Span.TrimStart('\uFEFF').Length;
                line = line[^trimmedLength..];
                _firstLine = false;
            }
            if (line.Span.StartsWith(":")) continue;
            var colon = line.Span.IndexOf(':');
            var field = colon < 0 ? line.Span : line.Span[..colon];
            var value = colon < 0 ? line[^0..] : line[(colon + 1)..];
            if (value.Span.StartsWith(" ")) value = value[1..];
            if (field.SequenceEqual("event")) name = value;
            if (!field.SequenceEqual("data")) continue;
            if (data is { } first)
            {
                multilineData ??= new StringBuilder().Append(first.Span);
                multilineData.Append('\n').Append(value.Span);
            }
            else data = value;
        }

        if (multilineData is not null) data = multilineData.ToString().AsMemory();
        var result = SseFrame.Decode(wire, name is { } eventName ? eventName.ToString() : "message", data, complete);
        if (_wire.Capacity > _retainedCharacters) _wire = new StringBuilder(_retainedCharacters);
        else _wire.Clear();
        _lineHasContent = false;
        _wireBytes = 0; _highSurrogate = false;
        return result;
    }
    private static InvalidDataException TooLarge() => new("SSE event exceeds configured MaxEventBytes; stream was not truncated.");

    public static async IAsyncEnumerable<SseFrame> ReadChunks(
        IAsyncEnumerable<string> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        SseFrameOptions? options = null)
    {
        var parser = new SseFrameReader(options);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            foreach (var frame in parser.Feed(chunk)) yield return frame;
        if (parser.Complete() is { } final) yield return final;
    }

    // Legacy IUpstreamClient and converter APIs accept physical lines without a newline as well
    // as newline-terminated lines/full WS SSE frames. Arbitrary chunk users call ReadChunks.
    public static async IAsyncEnumerable<SseFrame> ReadLines(
        IAsyncEnumerable<string> lines,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        SseFrameOptions? options = null)
    {
        var parser = new SseFrameReader(options);
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            foreach (var frame in parser.Feed(line)) yield return frame;
            if (!line.EndsWith('\n') && !line.EndsWith('\r'))
                foreach (var frame in parser.Feed("\n")) yield return frame;
        }
        if (parser.Complete() is { } final) yield return final;
    }

    public static async IAsyncEnumerable<string> WireFrames(
        IAsyncEnumerable<SseFrame> frames,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken)) yield return frame.WireText;
    }
}
