using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class NativeMcpResponseTests
{
    [Fact]
    public void ResponsesMcpCallToMessages_PreservesUseResultAndServer()
    {
        var converted = ProtocolConverter.ConvertResponse(
            ResponsesMcpResponse(), ProtocolConverter.Messages, ProtocolConverter.Responses, "public");

        var content = Assert.IsType<List<object?>>(converted["content"])
            .Select(item => Assert.IsType<Dictionary<string, object?>>(item))
            .ToList();
        var use = Assert.Single(content, block => block["type"]?.ToString() == "mcp_tool_use");
        Assert.Equal("weather", use["server_name"]);
        Assert.Equal("forecast", use["name"]);
        var result = Assert.Single(content, block => block["type"]?.ToString() == "mcp_tool_result");
        Assert.Equal("mcp_1", result["tool_use_id"]);
        Assert.False(Assert.IsType<bool>(result["is_error"]));
    }

    [Fact]
    public void MessagesMcpUseAndResultToResponses_BecomesCompletedMcpCall()
    {
        var converted = ProtocolConverter.ConvertResponse(
            new Dictionary<string, object?>
            {
                ["id"] = "msg_1",
                ["model"] = "claude",
                ["stop_reason"] = "end_turn",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mcp_tool_use",
                        ["id"] = "mcp_1",
                        ["name"] = "forecast",
                        ["server_name"] = "weather",
                        ["input"] = new Dictionary<string, object?> { ["city"] = "Shanghai" }
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mcp_tool_result",
                        ["tool_use_id"] = "mcp_1",
                        ["is_error"] = false,
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "text", ["text"] = "sunny" }
                        }
                    }
                },
                ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 1, ["output_tokens"] = 1 }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "public");

        var output = Assert.IsType<List<object?>>(converted["output"]);
        var call = Assert.IsType<Dictionary<string, object?>>(Assert.Single(output));
        Assert.Equal("mcp_call", call["type"]);
        Assert.Equal("weather", call["server_label"]);
        Assert.Equal("sunny", call["output"]);
        Assert.Equal("completed", call["status"]);
    }

    [Fact]
    public void ResponsesMcpCallToChat_IsExplicitlyRejected()
    {
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertResponse(
            ResponsesMcpResponse(), ProtocolConverter.Chat, ProtocolConverter.Responses, "public"));
    }

    private static Dictionary<string, object?> ResponsesMcpResponse()
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "resp_1",
            ["model"] = "gpt",
            ["created_at"] = 1,
            ["status"] = "completed",
            ["output"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "mcp_1",
                    ["type"] = "mcp_call",
                    ["server_label"] = "weather",
                    ["name"] = "forecast",
                    ["arguments"] = "{\"city\":\"Shanghai\"}",
                    ["output"] = "sunny",
                    ["status"] = "completed"
                }
            },
            ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 1, ["output_tokens"] = 1, ["total_tokens"] = 2 }
        };
    }
}
