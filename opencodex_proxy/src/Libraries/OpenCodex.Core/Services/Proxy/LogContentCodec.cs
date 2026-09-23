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

    /// <summary>
    /// Brotli 压缩被调用的次数。仅用于测试断言"已存在的块不会被重复压缩"。
    /// </summary>
    internal static long CompressionInvocationCount;

    public static EncodedLogContent Encode(string value)
    {
        var plan = Plan(value);
        var chunks = new List<EncodedLogContentChunk>(plan.Chunks.Count);
        foreach (var chunk in plan.Chunks)
        {
            chunks.Add(Compress(plan.Source, chunk));
        }

        return new EncodedLogContent(plan.OriginalLength, plan.Hash, chunks);
    }

    /// <summary>
    /// 只做分块与哈希,不做压缩。调用方据此查重后,再对缺失的块调用 <see cref="Compress"/>。
    /// </summary>
    /// <remarks>
    /// 同一会话反复发送时,正文前缀高度重复,绝大多数块在库中已存在。
    /// 先分块查重再压缩,可以避免对已存在的块重复执行 Brotli。
    /// </remarks>
    public static LogContentPlan Plan(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Plan(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// 只做分块与哈希。调用方已持有 UTF-8 字节时走这个重载,省掉一次字符串编解码。
    /// </summary>
    public static LogContentPlan Plan(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);

        var contentHash = Convert.ToHexStringLower(SHA256.HashData(utf8));
        if (utf8.Length == 0)
        {
            return new LogContentPlan(utf8, 0, contentHash, []);
        }

        var chunks = new List<LogChunkPlan>();
        foreach (var range in FindChunkRanges(utf8))
        {
            // 直接对切片求哈希,不再为每个块复制一份 byte[]。
            var hash = Convert.ToHexStringLower(SHA256.HashData(utf8.AsSpan(range.Offset, range.Length)));
            chunks.Add(new LogChunkPlan(range.Offset, range.Length, hash));
        }

        return new LogContentPlan(utf8, utf8.Length, contentHash, chunks);
    }

    /// <summary>
    /// 压缩单个块。仅应在查重确认该块尚不存在时调用。
    /// </summary>
    public static EncodedLogContentChunk Compress(byte[] source, LogChunkPlan chunk)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunk);

        var raw = source.AsSpan(chunk.Offset, chunk.Length);
        var compressed = Compress(raw);
        return compressed.Length < raw.Length
            ? new EncodedLogContentChunk(chunk.Hash, BrotliCodec, chunk.Length, compressed)
            : new EncodedLogContentChunk(chunk.Hash, RawCodec, chunk.Length, raw.ToArray());
    }

    public static string Decode(
        int originalLength,
        IReadOnlyList<StoredLogContentChunk> chunks)
    {
        return Decode(originalLength, chunks, out _);
    }

    /// <summary>
    /// 解码并顺带算出整份正文的 SHA-256。校验不再需要重新压缩一遍正文。
    /// </summary>
    public static string Decode(
        int originalLength,
        IReadOnlyList<StoredLogContentChunk> chunks,
        out string contentHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
        ArgumentNullException.ThrowIfNull(chunks);

        if (originalLength == 0)
        {
            if (chunks.Count != 0)
            {
                throw new InvalidDataException("Empty log content must not reference chunks.");
            }

            contentHash = Convert.ToHexStringLower(SHA256.HashData([]));
            return string.Empty;
        }

        using var output = new MemoryStream(originalLength);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks)
        {
            var raw = DecodeChunk(chunk);
            output.Write(raw);
            hasher.AppendData(raw);
        }

        if (output.Length != originalLength)
        {
            throw new InvalidDataException(
                $"Log content length mismatch: expected {originalLength}, got {output.Length}.");
        }

        contentHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
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

    private static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        Interlocked.Increment(ref CompressionInvocationCount);
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

/// <summary>
/// 一份正文的分块结果,尚未压缩。<see cref="Source"/> 保留 UTF-8 字节供后续压缩使用。
/// </summary>
internal sealed class LogContentPlan
{
    public LogContentPlan(
        byte[] source,
        int originalLength,
        string hash,
        IReadOnlyList<LogChunkPlan> chunks)
    {
        Source = source;
        OriginalLength = originalLength;
        Hash = hash;
        Chunks = chunks;
    }

    public byte[] Source { get; }

    public int OriginalLength { get; }

    public string Hash { get; }

    public IReadOnlyList<LogChunkPlan> Chunks { get; }
}

/// <summary>
/// 单个块的切片位置与内容哈希,尚未压缩。
/// </summary>
internal sealed class LogChunkPlan
{
    public LogChunkPlan(int offset, int length, string hash)
    {
        Offset = offset;
        Length = length;
        Hash = hash;
    }

    public int Offset { get; }

    public int Length { get; }

    public string Hash { get; }
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
