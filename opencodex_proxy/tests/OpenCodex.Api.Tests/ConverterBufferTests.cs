using OpenCodex.Core.Protocols;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class ConverterBufferTests
{
    [Fact]
    public void DecoderDropsConsumedRawPrefixWhileEscapesAndSurrogatesStillCrossFragments()
    {
        var decoder=new ApplyPatchJsonDeltaDecoder();Assert.Equal("",decoder.Append("{\"input\":\""));
        var part=new string('x',4096);
        for(var i=0;i<2048;i++) { Assert.Equal(part,decoder.Append(part));Assert.Equal(0,decoder.BufferedCharacters); }
        Assert.Equal("",decoder.Append("\\uD83D"));Assert.Equal("😀",decoder.Append("\\uDE00"));
        Assert.Equal("\n",decoder.Append("\\n"));Assert.Equal("",decoder.Append("\"}"));Assert.Equal(0,decoder.BufferedCharacters);
    }
}
