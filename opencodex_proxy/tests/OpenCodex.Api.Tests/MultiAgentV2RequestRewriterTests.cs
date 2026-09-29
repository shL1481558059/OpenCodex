using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentV2RequestRewriterTests
{
    [Fact]
    public void Apply_Passthrough_DoesNotRewritePayload()
    {
        var payload = V2Payload();

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Passthrough);

        Assert.Same(payload, result);
    }

    [Fact]
    public void Apply_Downgrade_PreservesPlaintextAgentMessagePayload()
    {
        var payload = V2Payload();
        payload["input"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "agent_message",
                ["id"] = "amsg_1",
                ["author"] = "/root/researcher",
                ["recipient"] = "/root",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "input_text",
                        ["text"] = "result text"
                    },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "encrypted_content",
                        ["encrypted_content"] = "secret"
                    }
                }
            }
        };

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Downgrade);

        Assert.False(result.ContainsKey("multi_agent"));
        var input = Assert.IsType<List<object?>>(result["input"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(input));
        Assert.Equal("message", message["type"]);
        Assert.Equal("user", message["role"]);
        Assert.Equal("amsg_1", message["id"]);
        var content = Assert.IsType<List<object?>>(message["content"]);
        Assert.Equal(2, content.Count);
        var first = Assert.IsType<Dictionary<string, object?>>(content[0]);
        Assert.Equal("input_text", first["type"]);
        Assert.Equal("result text", first["text"]);
        var second = Assert.IsType<Dictionary<string, object?>>(content[1]);
        Assert.Equal("input_text", second["type"]);
        Assert.Equal("secret", second["text"]);
    }

    [Fact]
    public void Apply_Downgrade_EncryptedAgentMessagePayloadBecomesPlaceholder()
    {
        var payload = V2Payload();
        payload["input"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "agent_message",
                ["author"] = "/root/researcher",
                ["recipient"] = "/root",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "encrypted_content",
                        ["encrypted_content"] = "enc_abc123"
                    }
                }
            }
        };

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Downgrade);

        var input = Assert.IsType<List<object?>>(result["input"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(input));
        var content = Assert.IsType<List<object?>>(message["content"]);
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        var text = Assert.IsType<string>(block["text"]);
        Assert.Contains("encrypted", text);
        Assert.Contains("/root/researcher", text);
        Assert.Contains("/root", text);
        Assert.DoesNotContain("enc_abc123", text);
    }

    [Fact]
    public void Apply_Downgrade_EmptyAgentMessageKeepsNonEmptyContent()
    {
        var payload = V2Payload();
        payload["input"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "agent_message",
                ["content"] = new List<object?>()
            }
        };

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Downgrade);

        var input = Assert.IsType<List<object?>>(result["input"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(input));
        var content = Assert.IsType<List<object?>>(message["content"]);
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        Assert.Equal("input_text", block["type"]);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(block["text"])));
    }

    [Fact]
    public void Apply_Downgrade_HostedCallBecomesTextMessageInsteadOfFunctionCall()
    {
        var payload = V2Payload();
        payload["input"] = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "multi_agent_call",
                ["call_id"] = "call_spawn_a",
                ["action"] = "spawn_agent",
                ["agent"] = new Dictionary<string, object?>
                {
                    ["agent_name"] = "/root"
                }
            }
        };

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Downgrade);

        var input = Assert.IsType<List<object?>>(result["input"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(input));
        Assert.Equal("message", message["type"]);
        Assert.NotEqual("function_call", message["type"]);
        var content = Assert.IsType<List<object?>>(message["content"]);
        var block = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        var text = Assert.IsType<string>(block["text"]);
        Assert.Contains("spawn_agent", text);
        Assert.Contains("call_spawn_a", text);
        Assert.Contains("/root", text);
    }

    [Fact]
    public void Apply_Downgrade_RemovesHostedToolTypes()
    {
        var payload = V2Payload();
        payload["tools"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "multi_agent" },
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = "spawn_agent"
            }
        };

        var result = MultiAgentV2RequestRewriter.Apply(payload, MultiAgentV2Action.Downgrade);

        var tools = Assert.IsType<List<object?>>(result["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(tools));
        Assert.Equal("function", tool["type"]);
        Assert.Equal("spawn_agent", tool["name"]);
    }

    private static Dictionary<string, object?> V2Payload()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "gpt-5.6-terra",
            ["multi_agent"] = new Dictionary<string, object?>
            {
                ["enabled"] = true
            }
        };
    }
}
