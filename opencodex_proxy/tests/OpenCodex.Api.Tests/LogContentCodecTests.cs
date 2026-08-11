using System.Text.Json;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class LogContentCodecTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("中文、emoji 😀、换行\n和引号\"")]
    public void EncodeDecode_RoundTripsExactText(string value)
    {
        var encoded = LogContentCodec.Encode(value);

        var decoded = LogContentCodec.Decode(
            encoded.OriginalLength,
            encoded.Chunks.Select(ToStoredChunk).ToList());

        Assert.Equal(value, decoded);
    }

    [Fact]
    public void AppendingConversation_ReusesStablePrefixChunks()
    {
        var original = BuildConversation(turnCount: 180);
        var appended = BuildConversation(turnCount: 181);

        var originalEncoded = LogContentCodec.Encode(original);
        var appendedEncoded = LogContentCodec.Encode(appended);
        var appendedHashes = appendedEncoded.Chunks.Select(chunk => chunk.Hash).ToHashSet(StringComparer.Ordinal);

        Assert.True(originalEncoded.Chunks.Count > 3);
        Assert.All(
            originalEncoded.Chunks.Take(originalEncoded.Chunks.Count - 1),
            chunk => Assert.Contains(chunk.Hash, appendedHashes));
        Assert.Equal(original, Decode(originalEncoded));
        Assert.Equal(appended, Decode(appendedEncoded));
    }

    [Fact]
    public void EditingEarlierTurn_ReusesChunksBeforeAndAfterEdit()
    {
        var messages = Enumerable.Range(0, 260)
            .Select(index => new Dictionary<string, object?>
            {
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = $"turn-{index:D4}:{DeterministicText(index, 280)}"
            })
            .ToList();
        var original = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "gpt-5",
            ["messages"] = messages
        });

        messages[80]["content"] = $"edited-turn-0080:{DeterministicText(9999, 420)}";
        var edited = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "gpt-5",
            ["messages"] = messages
        });

        var originalEncoded = LogContentCodec.Encode(original);
        var editedEncoded = LogContentCodec.Encode(edited);
        var originalHashes = originalEncoded.Chunks.Select(chunk => chunk.Hash).ToList();
        var editedHashes = editedEncoded.Chunks.Select(chunk => chunk.Hash).ToHashSet(StringComparer.Ordinal);
        var reusedIndexes = originalHashes
            .Select((hash, index) => (hash, index))
            .Where(item => editedHashes.Contains(item.hash))
            .Select(item => item.index)
            .ToList();

        Assert.Contains(reusedIndexes, index => index < originalEncoded.Chunks.Count / 4);
        Assert.Contains(reusedIndexes, index => index > originalEncoded.Chunks.Count / 2);
        Assert.Equal(edited, Decode(editedEncoded));
    }

    [Fact]
    public void RepeatedConversation_CompressesAndDeduplicatesPhysicalBytes()
    {
        var first = LogContentCodec.Encode(BuildConversation(turnCount: 160));
        var second = LogContentCodec.Encode(BuildConversation(turnCount: 161));
        var uniqueChunks = first.Chunks.Concat(second.Chunks)
            .GroupBy(chunk => chunk.Hash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var logicalBytes = first.OriginalLength + second.OriginalLength;
        var physicalBytes = uniqueChunks.Sum(chunk => chunk.Data.Length);

        Assert.True(
            physicalBytes < logicalBytes / 2,
            $"Expected physical bytes below 50% of logical bytes, got {physicalBytes}/{logicalBytes}.");
    }

    private static string BuildConversation(int turnCount)
    {
        var messages = Enumerable.Range(0, turnCount)
            .Select(index => new Dictionary<string, object?>
            {
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = $"turn-{index:D4}:{DeterministicText(index, 320)}"
            })
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "gpt-5",
            ["stream"] = true,
            ["messages"] = messages
        });
    }

    private static string DeterministicText(int seed, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[length];
        uint state = unchecked((uint)seed + 0x9e3779b9U);
        for (var index = 0; index < chars.Length; index++)
        {
            state = unchecked(state * 1664525U + 1013904223U);
            chars[index] = alphabet[(int)(state % alphabet.Length)];
        }

        return new string(chars);
    }

    private static StoredLogContentChunk ToStoredChunk(EncodedLogContentChunk chunk)
    {
        return new StoredLogContentChunk(
            chunk.Hash,
            chunk.Codec,
            chunk.OriginalLength,
            chunk.Data);
    }

    private static string Decode(EncodedLogContent content)
    {
        return LogContentCodec.Decode(
            content.OriginalLength,
            content.Chunks.Select(ToStoredChunk).ToList());
    }
}
