using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class NativeMcpConfigurationTests
{
    [Fact]
    public void ResponsesAllowedTools_BecomeAnthropicToolsetConfigs()
    {
        var converted = ProtocolConverter.ConvertRequest(
            ResponsesMcpRequest(new Dictionary<string, object?>
            {
                ["type"] = "mcp",
                ["server_label"] = "weather",
                ["server_url"] = "https://mcp.example.test",
                ["require_approval"] = "never",
                ["allowed_tools"] = new List<object?> { "forecast", "current" }
            }),
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "claude");

        var tool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(converted["tools"])));
        Assert.False(Assert.IsType<bool>(Assert.IsType<Dictionary<string, object?>>(tool["default_config"])["enabled"]));
        var configs = Assert.IsType<Dictionary<string, object?>>(tool["configs"]);
        Assert.Contains("forecast", configs.Keys);
        Assert.Contains("current", configs.Keys);
    }

    [Fact]
    public void ResponsesMcpApprovalRequirement_IsNotSilentlyDroppedForMessages()
    {
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            ResponsesMcpRequest(new Dictionary<string, object?>
            {
                ["type"] = "mcp",
                ["server_label"] = "weather",
                ["server_url"] = "https://mcp.example.test",
                ["require_approval"] = "always"
            }),
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "claude"));
    }

    [Fact]
    public void AnthropicEnabledConfigs_BecomeResponsesAllowedTools()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "claude",
                ["max_tokens"] = 100,
                ["messages"] = new List<object?>(),
                ["mcp_servers"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "url",
                        ["name"] = "weather",
                        ["url"] = "https://mcp.example.test"
                    }
                },
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "mcp_toolset",
                        ["mcp_server_name"] = "weather",
                        ["default_config"] = new Dictionary<string, object?> { ["enabled"] = false },
                        ["configs"] = new Dictionary<string, object?>
                        {
                            ["forecast"] = new Dictionary<string, object?> { ["enabled"] = true },
                            ["delete"] = new Dictionary<string, object?> { ["enabled"] = false }
                        }
                    }
                }
            },
            ProtocolConverter.Messages,
            ProtocolConverter.Responses,
            "gpt");

        var tool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(converted["tools"])));
        Assert.Equal("never", tool["require_approval"]);
        var allowed = Assert.IsType<List<object?>>(tool["allowed_tools"]);
        Assert.Equal("forecast", Assert.Single(allowed));
    }

    [Fact]
    public void AnthropicDisabledOverride_IsRejectedInsteadOfBroadeningResponsesAccess()
    {
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            AnthropicMcpRequest(
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp_toolset",
                    ["mcp_server_name"] = "weather",
                    ["default_config"] = new Dictionary<string, object?> { ["enabled"] = true },
                    ["configs"] = new Dictionary<string, object?>
                    {
                        ["delete"] = new Dictionary<string, object?> { ["enabled"] = false }
                    }
                }),
            ProtocolConverter.Messages,
            ProtocolConverter.Responses,
            "gpt"));
    }

    [Fact]
    public void ResponsesCompositeAllowedTools_IsRejectedWhenConstraintHasNoAnthropicEquivalent()
    {
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            ResponsesMcpRequest(new Dictionary<string, object?>
            {
                ["type"] = "mcp",
                ["server_label"] = "weather",
                ["server_url"] = "https://mcp.example.test",
                ["require_approval"] = "never",
                ["allowed_tools"] = new Dictionary<string, object?>
                {
                    ["tool_names"] = new List<object?> { "forecast" },
                    ["read_only"] = true
                }
            }),
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "claude"));
    }

    [Fact]
    public void AnthropicDisabledServerConfiguration_IsRejectedInsteadOfBroadeningResponsesAccess()
    {
        Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            AnthropicMcpRequest(
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp_toolset",
                    ["mcp_server_name"] = "weather"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["name"] = "weather",
                    ["url"] = "https://mcp.example.test",
                    ["tool_configuration"] = new Dictionary<string, object?> { ["enabled"] = false }
                }),
            ProtocolConverter.Messages,
            ProtocolConverter.Responses,
            "gpt"));
    }

    private static Dictionary<string, object?> ResponsesMcpRequest(Dictionary<string, object?> tool)
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "gpt",
            ["input"] = "hello",
            ["max_output_tokens"] = 100,
            ["tools"] = new List<object?> { tool }
        };
    }

    private static Dictionary<string, object?> AnthropicMcpRequest(
        Dictionary<string, object?> tool,
        Dictionary<string, object?>? server = null)
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "claude",
            ["max_tokens"] = 100,
            ["messages"] = new List<object?>(),
            ["mcp_servers"] = new List<object?>
            {
                server ?? new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["name"] = "weather",
                    ["url"] = "https://mcp.example.test"
                }
            },
            ["tools"] = new List<object?> { tool }
        };
    }
}
