using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class NativeMcpHistoryTests
{
    [Fact]
    public void ResponsesMcpCallHistory_ToMessages_PreservesNativeBlocks()
    {
        var converted = ProtocolConverter.ConvertRequest(new Dictionary<string, object?>
        {
            ["model"] = "local",
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp_call", ["id"] = "mcp_1", ["server_label"] = "weather",
                    ["name"] = "forecast", ["arguments"] = "{\"city\":\"Shanghai\"}", ["output"] = "sunny"
                }
            }
        }, ProtocolConverter.Responses, ProtocolConverter.Messages, "upstream");

        var messages = Assert.IsType<List<object?>>(converted["messages"]);
        var assistant = Assert.IsType<Dictionary<string, object?>>(messages[0]);
        var use = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(assistant["content"])));
        Assert.Equal("mcp_tool_use", use["type"]);
        Assert.Equal("weather", use["server_name"]);
        var user = Assert.IsType<Dictionary<string, object?>>(messages[1]);
        var result = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(user["content"])));
        Assert.Equal("mcp_tool_result", result["type"]);
    }

    [Fact]
    public void MessagesMcpHistory_ToResponses_PreservesNativeItem()
    {
        var converted = ProtocolConverter.ConvertRequest(MessagesHistory(), ProtocolConverter.Messages, ProtocolConverter.Responses, "upstream");
        var input = Assert.IsType<List<object?>>(converted["input"]);
        var call = Assert.Single(input, item => item is Dictionary<string, object?> value && Equals(value["type"], "mcp_call"));
        var typed = Assert.IsType<Dictionary<string, object?>>(call);
        Assert.Equal("weather", typed["server_label"]);
        Assert.Equal("sunny", typed["output"]);
        Assert.DoesNotContain(input, item => item is Dictionary<string, object?> value && Equals(value["type"], "function_call"));
    }

    [Fact]
    public void NativeMcpHistory_ToChat_IsRejected()
    {
        var exception = Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            MessagesHistory(), ProtocolConverter.Messages, ProtocolConverter.Chat, "upstream"));
        Assert.Contains("native MCP history", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> MessagesHistory() => new()
    {
        ["model"] = "local", ["max_tokens"] = 100,
        ["messages"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["role"] = "assistant", ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mcp_tool_use", ["id"] = "mcp_1", ["server_name"] = "weather",
                        ["name"] = "forecast", ["input"] = new Dictionary<string, object?> { ["city"] = "Shanghai" }
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["role"] = "user", ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mcp_tool_result", ["tool_use_id"] = "mcp_1", ["content"] = "sunny", ["is_error"] = false
                    }
                }
            }
        }
    };
}
