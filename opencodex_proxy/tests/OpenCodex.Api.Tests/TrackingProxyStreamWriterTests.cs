using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class TrackingProxyStreamWriterTests
{
    [Fact]
    public async Task WriteLines_MeaningfulOnly_SyntheticSkeletonDoesNotCountAsWritten()
    {
        var inner = new RecordingWriter();
        var writer = new TrackingProxyStreamWriter(inner, countOnlyMeaningfulWrites: true);

        await writer.WriteLinesAsync(
            ToAsyncEnumerable(
                "event: response.created\ndata: {}\n\n",
                "",
                "event: response.in_progress\ndata: {}\n\n"),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.False(writer.HasWritten);

        await writer.WriteLinesAsync(
            ToAsyncEnumerable("event: response.output_item.added\ndata: {}\n\n"),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.True(writer.HasWritten);
    }

    [Fact]
    public async Task WriteLines_DefaultMode_AnyLineCountsAsWritten()
    {
        var inner = new RecordingWriter();
        var writer = new TrackingProxyStreamWriter(inner);

        await writer.WriteLinesAsync(
            ToAsyncEnumerable(
                "event: response.created\ndata: {}\n\n",
                ""),
            static _ => true,
            static () => 1,
            CancellationToken.None);

        Assert.True(writer.HasWritten);
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private sealed class RecordingWriter : IProxyStreamWriter
    {
        public List<string> WrittenLines { get; } = [];

        public void PrepareSse()
        {
        }

        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines,
            Func<string, bool> countsForTtft,
            Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                WrittenLines.Add(line);
            }

            return new StreamWriteMetrics();
        }
    }
}
