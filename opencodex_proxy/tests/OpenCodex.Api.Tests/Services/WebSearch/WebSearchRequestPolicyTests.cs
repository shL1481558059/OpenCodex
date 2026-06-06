using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.WebSearch;

public sealed class WebSearchRequestPolicyTests
{
    [Fact]
    public void DeclaresWebSearchToolReturnsTrueWhenToolsContainWebSearch()
    {
        var payload = new Dictionary<string, object?>
        {
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "web_search"
                }
            }
        };

        Assert.True(WebSearchRequestPolicy.DeclaresWebSearchTool(payload));
    }

    [Theory]
    [MemberData(nameof(PayloadsWithoutWebSearchTool))]
    public void DeclaresWebSearchToolReturnsFalseWhenToolIsAbsent(Dictionary<string, object?> payload)
    {
        Assert.False(WebSearchRequestPolicy.DeclaresWebSearchTool(payload));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 5)]
    [InlineData(2, 2)]
    [InlineData(3L, 3)]
    [InlineData(4.9, 4)]
    [InlineData("6", 6)]
    [InlineData(-1, 0)]
    [InlineData("invalid", 5)]
    public void MaxWebSearchCallsPreservesPythonCompatibleDefaultsAndCoercion(object? value, int expected)
    {
        var payload = new Dictionary<string, object?>();
        if (value is not null)
        {
            payload["max_tool_calls"] = value;
        }

        Assert.Equal(expected, WebSearchRequestPolicy.MaxWebSearchCalls(payload));
    }

    [Fact]
    public void ParseQueryReturnsTrimmedQuery()
    {
        var (query, error) = WebSearchRequestPolicy.ParseQuery("{\"query\":\"  OpenAI  \"}");

        Assert.Equal("OpenAI", query);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("[]", "web_search arguments must be an object")]
    [InlineData("{", "web_search arguments must be valid JSON")]
    [InlineData("{\"query\":\"OpenAI\",\"extra\":true}", "web_search only supports the query argument")]
    [InlineData("", "web_search query is required")]
    [InlineData("{}", "web_search query is required")]
    [InlineData("{\"query\":\"   \"}", "web_search query is required")]
    public void ParseQueryReturnsCompatibleErrors(string arguments, string expectedError)
    {
        var (query, error) = WebSearchRequestPolicy.ParseQuery(arguments);

        Assert.Null(query);
        Assert.Equal(expectedError, error);
    }

    public static TheoryData<Dictionary<string, object?>> PayloadsWithoutWebSearchTool()
    {
        return new TheoryData<Dictionary<string, object?>>
        {
            new(),
            new()
            {
                ["tools"] = "web_search"
            },
            new()
            {
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function"
                    }
                }
            },
            new()
            {
                ["tools"] = new List<object?>
                {
                    "web_search"
                }
            }
        };
    }
}
