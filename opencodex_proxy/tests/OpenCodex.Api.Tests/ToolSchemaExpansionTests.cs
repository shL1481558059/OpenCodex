using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ToolSchemaExpansionTests
{
    private static Dictionary<string, object?> SelfReferentialResponsesRequest()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["root"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/Node" }
            },
            ["$defs"] = new Dictionary<string, object?>
            {
                ["Node"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["value"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["left"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/Node" },
                        ["right"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/Node" }
                    }
                }
            }
        };

        return new Dictionary<string, object?>
        {
            ["model"] = "local",
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_text",
                            ["text"] = "hi"
                        }
                    }
                }
            },
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = "recursive_tool",
                    ["description"] = "self-referential schema",
                    ["parameters"] = parameters
                }
            }
        };
    }

    [Fact]
    public void ConvertRequest_SelfReferentialToolSchema_ExpandsWithoutExploding()
    {
        var request = ProtocolConverter.ConvertRequest(
            SelfReferentialResponsesRequest(),
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        var tools = Assert.IsType<List<object?>>(request["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(tools[0]);
        var inputSchema = Assert.IsType<Dictionary<string, object?>>(tool["input_schema"]);

        Assert.False(inputSchema.ContainsKey("$defs"));
        var json = JsonSerializer.Serialize(inputSchema);
        Assert.DoesNotContain("$ref", json);
        Assert.True(json.Length < 200_000, $"展开后的 schema 体积异常膨胀: {json.Length} 字节");
    }

    [Fact]
    public void ConvertRequest_NonRecursiveToolSchema_StillInlinesRefs()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["color"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/Color" }
            },
            ["$defs"] = new Dictionary<string, object?>
            {
                ["Color"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new List<object?> { "red", "green", "blue" }
                }
            }
        };

        var responses = new Dictionary<string, object?>
        {
            ["model"] = "local",
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_text",
                            ["text"] = "hi"
                        }
                    }
                }
            },
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = "paint",
                    ["parameters"] = parameters
                }
            }
        };

        var request = ProtocolConverter.ConvertRequest(
            responses,
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        var tools = Assert.IsType<List<object?>>(request["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(tools[0]);
        var inputSchema = Assert.IsType<Dictionary<string, object?>>(tool["input_schema"]);
        var properties = Assert.IsType<Dictionary<string, object?>>(inputSchema["properties"]);
        var color = Assert.IsType<Dictionary<string, object?>>(properties["color"]);

        Assert.Equal("string", color["type"]);
        var json = JsonSerializer.Serialize(inputSchema);
        Assert.DoesNotContain("$ref", json);
        Assert.Contains("red", json);
    }
}
