using OpenCodex.Api.DTOs.AdminWebSearch;

namespace OpenCodex.Api.Tests.DTOs.AdminWebSearch;

public sealed class WebSearchTestKeyRequestTests
{
    [Fact]
    public void FromParsesKeyIdAndTrimsQuery()
    {
        var request = WebSearchTestKeyRequest.From(new Dictionary<string, object?>
        {
            ["id"] = " 42 ",
            ["query"] = " OpenAI news "
        });

        Assert.Equal(42, request.KeyId);
        Assert.Equal("OpenAI news", request.Query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromDefaultsBlankQueryToOpenAI(object? query)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = 7
        };
        if (query is not null)
        {
            body["query"] = query;
        }

        var request = WebSearchTestKeyRequest.From(body);

        Assert.Equal(7, request.KeyId);
        Assert.Equal("OpenAI", request.Query);
    }

    [Fact]
    public void FromReturnsNullKeyIdForInvalidId()
    {
        var request = WebSearchTestKeyRequest.From(new Dictionary<string, object?>
        {
            ["id"] = "missing"
        });

        Assert.Null(request.KeyId);
    }
}
