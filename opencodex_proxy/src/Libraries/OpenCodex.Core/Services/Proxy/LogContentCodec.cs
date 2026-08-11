using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OpenCodex.Core.Services.Proxy;

internal static class LogContentCodec
{
    internal const string BrotliCodec = "br";
    internal const string RawCodec = "raw";

    private const int MinimumChunkBytes = 2 * 1024;
    private const int AverageChunkBytes = 8 * 1024;
    private const int MaximumChunkBytes = 32 * 1024;
    private static readonly ulong[] GearTable = BuildGearTable();

    public static EncodedLogContent Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var source = Encoding.UTF8.GetBytes(value);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(source));
        if (source.Length == 0)
        {
            return new EncodedLogContent(0, contentHash, []);
        }

        var chunks = new List<EncodedLogContentChunk>();
        foreach (var range in FindChunkRanges(source))
        {
            var raw = source.AsSpan(range.Offset, range.Length).ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
            var compressed = Compress(raw);
            if (compressed.Length < raw.Length)
            {
                chunks.Add(new EncodedLogContentChunk(
                    hash,
                    BrotliCodec,
                    raw.Length,
                    compressed));
            }
            else
            {
                chunks.Add(new EncodedLogContentChunk(
                    hash,
                    RawCodec,
                    raw.Length,
                    raw));
            }
        }

        return new EncodedLogContent(source.Length, contentHash, chunks);
    }

    public static string Decode(
        int originalLength,
        IReadOnlyList<StoredLogContentChunk> chunks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        ArgumentNullException.ThrowIfNull(chunks);

        if (originalLength == 0)
        {
            if (chunks.Count != 0)
            {
                throw new InvalidDataException("Empty log content must not reference chunks.");
            }

            return string.Empty;
        }

        using var output = new MemoryStream(originalLength);
        foreach (var chunk in chunks)
        {
            var raw = DecodeChunk(chunk);
            output.Write(raw);
        }

        if (output.Length != originalLength)
        {
            throw new InvalidDataException(
                $"Log content length mismatch: expected {originalLength}, got {output.Length}.");
        }

        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, originalLength);
    }

    private static byte[] DecodeChunk(StoredLogContentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        byte[] raw;
        switch (chunk.Codec)
        {
            case RawCodec:
                raw = chunk.Data;
                break;
            case BrotliCodec:
                using (var input = new MemoryStream(chunk.Data, writable: false))
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream(chunk.OriginalLength))
                {
                    brotli.CopyTo(output);
                    raw = output.ToArray();
                }
                break;
            default:
                throw new InvalidDataException($"Unsupported log content codec '{chunk.Codec}'.");
        }

        if (raw.Length != chunk.OriginalLength)
        {
            throw new InvalidDataException(
                $"Log chunk length mismatch for {chunk.Hash}: expected {chunk.OriginalLength}, got {raw.Length}.");
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (!string.Equals(actualHash, chunk.Hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Log chunk hash mismatch for {chunk.Hash}.");
        }

        return raw;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return output.ToArray();
    }

    private static IEnumerable<ChunkRange> FindChunkRanges(byte[] source)
    {
        var start = 0;
        while (start < source.Length)
        {
            var maximumEnd = Math.Min(source.Length, start + MaximumChunkBytes);
            if (maximumEnd - start <= MinimumChunkBytes)
            {
                yield return new ChunkRange(start, maximumEnd - start);
                yield break;
            }

            ulong fingerprint = 0;
            var end = start;
            while (end < maximumEnd)
            {
                fingerprint = unchecked((fingerprint << 1) + GearTable[source[end]]);
                end++;

                var length = end - start;
                if (length >= MinimumChunkBytes
                    && ((fingerprint & (AverageChunkBytes - 1)) == 0
                        || length == MaximumChunkBytes))
                {
                    break;
                }
            }

            yield return new ChunkRange(start, end - start);
            start = end;
        }
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x6a09e667f3bcc909UL;
        for (var index = 0; index < table.Length; index++)
        {
            state = unchecked(state + 0x9e3779b97f4a7c15UL);
            var value = state;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            table[index] = value ^ (value >> 31);
        }

        return table;
    }

    private sealed class ChunkRange
    {
        public ChunkRange(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        public int Offset { get; }

        public int Length { get; }
    }
}

internal sealed class EncodedLogContent
{
    public EncodedLogContent(
        int originalLength,
        string hash,
        IReadOnlyList<EncodedLogContentChunk> chunks)
    {
        OriginalLength = originalLength;
        Hash = hash;
        Chunks = chunks;
    }

    public int OriginalLength { get; }

    public string Hash { get; }

    public IReadOnlyList<EncodedLogContentChunk> Chunks { get; }
}

internal sealed class EncodedLogContentChunk
{
    public EncodedLogContentChunk(
        string hash,
        string codec,
        int originalLength,
        byte[] data)
    {
        Hash = hash;
        Codec = codec;
        OriginalLength = originalLength;
        Data = data;
    }

    public string Hash { get; }

    public string Codec { get; }

    public int OriginalLength { get; }

    public byte[] Data { get; }
}

internal sealed class StoredLogContentChunk
{
    public StoredLogContentChunk(
        string hash,
        string codec,
        int originalLength,
        byte[] data)
    {
        Hash = hash;
        Codec = codec;
        OriginalLength = originalLength;
        Data = data;
    }

    public string Hash { get; }

    public string Codec { get; }

    public int OriginalLength { get; }

    public byte[] Data { get; }
}
