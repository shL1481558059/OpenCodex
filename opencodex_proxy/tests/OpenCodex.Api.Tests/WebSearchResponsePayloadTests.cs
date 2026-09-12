using System.Text.Json;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchResponsePayloadTests
{
    [Fact]
    public void JsonOutput_IsNotChangedBySourceAnnotations()
    {
        const string json = "{\"answer\":\"hello\"}";
        var response = Response(json);

        WebSearchResponsePayload.AddSourceAnnotations(response, [Result()]);

        Assert.Equal(json, TextBlock(response)["text"]);
        using var document = JsonDocument.Parse(StringValue(TextBlock(response), "text"));
        Assert.Equal("hello", document.RootElement.GetProperty("answer").GetString());
    }

    [Fact]
    public void Citations_UseUnicodeCharacterOffsetsAndOnlyReferencedSources()
    {
        const string url = "https://example.com/source";
        var response = Response($"你好 😀 {url}");

        WebSearchResponsePayload.AddSourceAnnotations(response, [Result(), Result()]);

        var citation = Assert.IsType<Dictionary<string, object?>>(Assert.Single(ListValue(TextBlock(response), "annotations")));
        Assert.Equal(5, citation["start_index"]);
        Assert.Equal(5 + url.Length, citation["end_index"]);
        Assert.Equal(url, citation["url"]);
    }

    [Fact]
    public void Sources_RespectIncludeAndRejectUnsafeSchemes()
    {
        var result = Result();
        result.ItemId = "ws_ocxp_test";
        var withoutSources = WebSearchResponsePayload.BuildWebSearchItem(result, true);
        var withSources = WebSearchResponsePayload.BuildWebSearchItem(result, true, true);

        Assert.False(ObjectValue(withoutSources, "action").ContainsKey("sources"));
        Assert.Equal("ws_ocxp_test", withSources["id"]);
        var sources = ListValue(ObjectValue(withSources, "action"), "sources");
        Assert.Equal(2, sources.Count);
        Assert.All(sources.Cast<Dictionary<string, object?>>(),
            source => Assert.StartsWith("https://", StringValue(source, "url"), StringComparison.Ordinal));
    }

    private static Dictionary<string, object?> Response(string text) => new()
    {
        ["output"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "output_text", ["text"] = text, ["annotations"] = new List<object?>()
                    }
                }
            }
        }
    };

    private static Dictionary<string, object?> TextBlock(Dictionary<string, object?> response) =>
        (Dictionary<string, object?>)ListValue((Dictionary<string, object?>)ListValue(response, "output")[0]!, "content")[0]!;

    private static WebSearchToolResult Result()
    {
        var payload = new Dictionary<string, object?>
        {
            ["answer"] = "answer",
            ["results"] = new List<object?>
            {
                new Dictionary<string, object?> { ["title"] = "Source", ["url"] = "https://example.com/source" },
                new Dictionary<string, object?> { ["title"] = "Unused", ["url"] = "https://example.com/unused" },
                new Dictionary<string, object?> { ["title"] = "Unsafe", ["url"] = "javascript:alert(1)" }
            }
        };
        return new WebSearchToolResult("call_1", "query", "completed", JsonDumps(payload), payload,
            null, "test", null, null, null, null, null, 200, null);
    }
}
