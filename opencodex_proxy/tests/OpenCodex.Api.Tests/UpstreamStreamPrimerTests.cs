using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class UpstreamStreamPrimerTests
{
    [Fact]
    public async Task PrimeAsync_UpstreamThrowsBeforeFirstLine_PropagatesWithoutYielding()
    {
        var thrown = await Assert.ThrowsAsync<UpstreamException>(() =>
            UpstreamStreamPrimer.PrimeAsync(
                ThrowBeforeFirstLine(),
                CancellationToken.None));

        Assert.Equal(ProxyHttpStatus.TooManyRequests, thrown.StatusCode);
    }

    [Fact]
    public async Task PrimeAsync_ReplaysFirstLineThenRemainingLines()
    {
        var primed = await UpstreamStreamPrimer.PrimeAsync(
            ToAsyncEnumerable("first", "second"),
            CancellationToken.None);

        var lines = new List<string>();
        await foreach (var line in primed)
        {
            lines.Add(line);
        }

        Assert.Equal(["first", "second"], lines);
    }

    private static async IAsyncEnumerable<string> ThrowBeforeFirstLine()
    {
        await Task.CompletedTask;
        throw new UpstreamException(
            "upstream returned HTTP 429",
            ProxyHttpStatus.TooManyRequests);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<string> ToAsyncEnumerable(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}
