using System.Text.Json;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.WebSearch;

public sealed class WebSearchToolCallParserTests
{
    [Fact]
    public void ExtractToolCallsReadsChatFunctionToolCalls()
    {
        var rawToolCall = new Dictionary<string, object?>
        {
            ["id"] = "call_web",
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = "web_search",
                ["arguments"] = "{\"query\":\"OpenAI\"}"
            }
        };
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new List<object?>
                        {
                            "ignored",
                            rawToolCall
                        }
                    }
                }
            }
        };

        var call = Assert.Single(WebSearchToolCallParser.ExtractToolCalls(payload, ProtocolConverter.Chat));

        Assert.Equal("call_web", call.Id);
        Assert.Equal(0, call.Index);
        Assert.Equal("web_search", call.Name);
        Assert.Equal("{\"query\":\"OpenAI\"}", call.Arguments);
        Assert.NotSame(rawToolCall, call.Raw);
        Assert.Equal("function", call.Raw["type"]);
    }

    [Fact]
    public void ExtractToolCallsReadsMessagesToolUseBlocks()
    {
        var rawBlock = new Dictionary<string, object?>
        {
            ["type"] = "tool_use",
            ["id"] = "toolu_web",
            ["name"] = "web_search",
            ["input"] = new Dictionary<string, object?>
            {
                ["query"] = "OpenAI"
            }
        };
        var payload = new Dictionary<string, object?>
        {
            ["content"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = "ignored"
                },
                rawBlock
            }
        };

        var call = Assert.Single(WebSearchToolCallParser.ExtractToolCalls(payload, ProtocolConverter.Messages));

        Assert.Equal("toolu_web", call.Id);
        Assert.Equal(0, call.Index);
        Assert.Equal("web_search", call.Name);
        using var arguments = JsonDocument.Parse(call.Arguments);
        Assert.Equal("OpenAI", arguments.RootElement.GetProperty("query").GetString());
        Assert.NotSame(rawBlock, call.Raw);
        Assert.Equal("tool_use", call.Raw["type"]);
    }

    [Fact]
    public void ExtractToolCallsReturnsEmptyForUnsupportedProtocol()
    {
        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = "call_web"
                            }
                        }
                    }
                }
            }
        };

        var calls = WebSearchToolCallParser.ExtractToolCalls(payload, ProtocolConverter.Responses);

        Assert.Empty(calls);
    }

    [Fact]
    public void ExtractMessagesToolCallsUsesEmptyObjectArgumentsWhenInputIsMissing()
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_web",
                    ["name"] = "web_search"
                }
            }
        };

        var call = Assert.Single(WebSearchToolCallParser.ExtractMessagesToolCalls(payload));

        Assert.Equal("{}", call.Arguments);
    }
}
