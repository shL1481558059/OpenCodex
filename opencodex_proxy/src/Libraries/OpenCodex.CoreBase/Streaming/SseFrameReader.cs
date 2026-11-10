using System.Runtime.CompilerServices;
using System.Text;

namespace OpenCodex.Core.Protocols;

public sealed class SseFrameOptions
{
    // A bounded event, not a bound on the response. Configurable by transport/writer callers.
    public int MaxEventBytes { get; init; } = 16 * 1024 * 1024;
}

public sealed class SseTransportOptions
{
    public int MaxEventBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxProbeBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxProbeFrames { get; init; } = 1024;
    public int MaxWebSocketMessageBytes { get; init; } = 32 * 1024 * 1024;
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
    private readonly StringBuilder _wire = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _name = "message";
    private bool _hasData;
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
        foreach (var ch in chunk)
        {
            if (_afterCr && ch == '\n') { _afterCr = false; continue; }
            _afterCr = ch == '\r';
            if (ch is '\r' or '\n')
            {
                CountByteBudget('\n');
                _wire.Append('\n');
                var frame = EndLine();
                if (frame is not null) yield return frame;
            }
            else
            {
                CountByteBudget(ch);
                _line.Append(ch);
                _wire.Append(ch);
            }
        }
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
        if (_line.Length > 0) ReadField();
        return Take(complete: false);
    }

    private SseFrame? EndLine()
    {
        if (_line.Length == 0) return Take(complete: true);
        ReadField();
        return null;
    }

    private void ReadField()
    {
        var line = _line.ToString();
        _line.Clear();
        if (_firstLine) { line = line.TrimStart('\uFEFF'); _firstLine = false; }
        if (line.StartsWith(':')) return;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..]; // SSE removes exactly one space.
        if (field == "event") _name = value;
        if (field == "data")
        {
            if (_hasData) _data.Append('\n');
            _data.Append(value);
            _hasData = true;
        }
    }

    private SseFrame Take(bool complete)
    {
        var wire = _wire.ToString();
        var result = SseFrame.Decode(wire, _name, _hasData ? _data.ToString() : null, complete);
        _wire.Clear(); _line.Clear(); _data.Clear(); _hasData = false; _name = "message";
        _wireBytes = 0; _highSurrogate = false;
        // Do not permanently retain a large completed-event capacity on long-lived streams.
        if (_wire.Capacity > 65536) _wire.Capacity = 65536;
        if (_data.Capacity > 65536) _data.Capacity = 65536;
        if (_line.Capacity > 65536) _line.Capacity = 65536;
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
