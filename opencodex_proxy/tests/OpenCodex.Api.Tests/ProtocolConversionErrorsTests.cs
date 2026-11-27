using OpenCodex.Core.ProtocolConversion;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProtocolConversionErrorsTests
{
    [Fact]
    public void Wrap_PreservesProtocolExceptionStatusCodes()
    {
        var badRequest = Assert.Throws<OpenCodex.Core.Errors.BadRequestException>(() =>
            ProtocolConversionErrors.Wrap(() =>
                throw new OpenCodex.ProtocolConversion.Errors.BadRequestException("invalid request", 422)));
        Assert.Equal(422, badRequest.StatusCode);

        var upstream = Assert.Throws<OpenCodex.Core.Errors.UpstreamException>(() =>
            ProtocolConversionErrors.Wrap(() =>
                throw new OpenCodex.ProtocolConversion.Errors.UpstreamException(
                    "rate limited", "rate_limit_exceeded", 429)));
        Assert.Equal(429, upstream.StatusCode);
        Assert.Equal("rate_limit_exceeded", upstream.ErrorCode);
    }

    [Fact]
    public async Task WrapStream_PreservesProtocolExceptionStatusCodes()
    {
        await using var frames = ProtocolConversionErrors.WrapStream(FailAsync()).GetAsyncEnumerator();
        try
        {
            var upstream = await Assert.ThrowsAsync<OpenCodex.Core.Errors.UpstreamException>(
                async () => await frames.MoveNextAsync());
            Assert.Equal(503, upstream.StatusCode);
            Assert.Equal("upstream_unavailable", upstream.ErrorCode);
        }
        finally
        {
            await frames.DisposeAsync();
        }
    }

    private static async IAsyncEnumerable<int> FailAsync()
    {
        await Task.Yield();
        throw new OpenCodex.ProtocolConversion.Errors.UpstreamException(
            "unavailable", "upstream_unavailable", 503);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
