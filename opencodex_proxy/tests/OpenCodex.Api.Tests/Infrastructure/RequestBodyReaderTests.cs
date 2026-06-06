using System.Text;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Infrastructure;

namespace OpenCodex.Api.Tests.Infrastructure;

public sealed class RequestBodyReaderTests
{
    [Fact]
    public async Task ReadJsonObjectReturnsNestedObjectsListsAndStableNumberTypes()
    {
        var request = JsonRequest("""
            {
              "small": 30,
              "large": 2147483648,
              "fraction": 1.5,
              "nested": { "enabled": true },
              "items": [{ "id": "chat" }]
            }
            """);
        var reader = new RequestBodyReader();

        var result = await reader.ReadJsonObjectAsync(request);

        Assert.NotNull(result);
        Assert.IsType<int>(result["small"]);
        Assert.Equal(30, result["small"]);
        Assert.IsType<long>(result["large"]);
        Assert.Equal(2147483648L, result["large"]);
        Assert.IsType<double>(result["fraction"]);
        Assert.Equal(1.5, result["fraction"]);

        var nested = Assert.IsType<Dictionary<string, object?>>(result["nested"]);
        Assert.True((bool)nested["enabled"]!);
        var items = Assert.IsType<List<object?>>(result["items"]);
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(items));
        Assert.Equal("chat", item["id"]);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("not-json")]
    public async Task ReadJsonObjectReturnsNullForNonObjectOrInvalidJson(string body)
    {
        var reader = new RequestBodyReader();

        var result = await reader.ReadJsonObjectAsync(JsonRequest(body));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadFormOrJsonObjectReadsFormFieldsAsStrings()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("username=admin&password=pw"));
        var reader = new RequestBodyReader();

        var result = await reader.ReadFormOrJsonObjectAsync(context.Request);

        Assert.Equal("admin", result["username"]);
        Assert.Equal("pw", result["password"]);
    }

    [Fact]
    public async Task ReadFormOrJsonObjectReturnsEmptyDictionaryForInvalidJson()
    {
        var reader = new RequestBodyReader();

        var result = await reader.ReadFormOrJsonObjectAsync(JsonRequest("not-json"));

        Assert.Empty(result);
    }

    private static HttpRequest JsonRequest(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }
}
