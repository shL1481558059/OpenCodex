using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class NativeMcpProtocolTests
{
    [Fact]
    public void ResponsesNativeMcpToChat_IsRejectedInsteadOfBecomingFakeFunction()
    {
        var exception = Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            ResponsesRequest(
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp",
                    ["server_label"] = "github",
                    ["server_url"] = "https://mcp.example.test/github",
                    ["allowed_tools"] = new List<object?> { "search_repositories" },
                    ["require_approval"] = "never"
                }),
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream"));

        Assert.Contains("native remote MCP tool 'github'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be converted to chat", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnthropicMcpToolset_WithServerDefinition_EnrichesCanonicalTool()
    {
        var canonical = ProtocolConverter.AnthropicToolsToCanonical(
            new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp_toolset",
                    ["mcp_server_name"] = "github"
                }
            },
            new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["name"] = "github",
                    ["url"] = "https://mcp.example.test/github",
                    ["authorization_token"] = "secret",
                    ["tool_configuration"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["allowed_tools"] = new List<object?> { "search_repositories" }
                    }
                }
            });

        var tool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(canonical));
        Assert.True(ProtocolConverter.IsNativeRemoteMcpCanonicalTool(tool));
        Assert.False(ProtocolConverter.IsLegacyNamespaceMcpCanonicalTool(tool));
        Assert.Equal("github", tool["server_label"]);
        Assert.Equal("https://mcp.example.test/github", tool["server_url"]);
        Assert.Equal("secret", tool["authorization"]);

        var servers = ProtocolConverter.BuildAnthropicMcpServers(canonical);
        var server = Assert.IsType<Dictionary<string, object?>>(Assert.Single(servers));
        Assert.Equal("url", server["type"]);
        Assert.Equal("github", server["name"]);
        Assert.Equal("https://mcp.example.test/github", server["url"]);
        Assert.Equal("secret", server["authorization_token"]);
    }

    [Fact]
    public void ResponsesNativeMcpToMessages_EmitsMcpToolsetWithoutFunctionWrapper()
    {
        var request = ProtocolConverter.ConvertRequest(
            ResponsesRequest(
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp",
                    ["server_label"] = "github",
                    ["server_url"] = "https://mcp.example.test/github",
                    ["authorization"] = "secret"
                }),
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        var tool = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<List<object?>>(request["tools"])));
        Assert.Equal("mcp_toolset", tool["type"]);
        Assert.Equal("github", tool["mcp_server_name"]);
        Assert.False(tool.ContainsKey("name"));
        Assert.False(tool.ContainsKey("input_schema"));
    }

    [Fact]
    public void LegacyNamespaceMcp_RemainsFlattenedFunctionForChat()
    {
        var request = ProtocolConverter.ConvertRequest(
            ResponsesRequest(
                new Dictionary<string, object?>
                {
                    ["type"] = "namespace",
                    ["name"] = "mcp__computer_use",
                    ["tools"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "function",
                            ["name"] = "click",
                            ["parameters"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object",
                                ["properties"] = new Dictionary<string, object?>()
                            }
                        }
                    }
                }),
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var wrapper = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<List<object?>>(request["tools"])));
        Assert.Equal("function", wrapper["type"]);
        var function = Assert.IsType<Dictionary<string, object?>>(wrapper["function"]);
        Assert.Equal("mcp__computer_use__click", function["name"]);
    }

    [Fact]
    public void AnthropicConnectorWithoutServerUrl_CannotBeConvertedToResponses()
    {
        var canonical = ProtocolConverter.AnthropicToolsToCanonical(
            new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mcp_toolset",
                    ["mcp_server_name"] = "missing-server"
                }
            },
            null);

        var exception = Assert.Throws<BadRequestException>(() =>
            ProtocolConverter.EnsureRemoteMcpToolsConvertible(canonical, ProtocolConverter.Responses));

        Assert.Contains("requires server_label and one of server_url", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> ResponsesRequest(Dictionary<string, object?> tool)
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "local",
            ["input"] = "Use the configured tool.",
            ["tools"] = new List<object?> { tool }
        };
    }
}
