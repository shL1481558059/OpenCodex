using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentV2PolicyTests
{
    [Fact]
    public void Resolve_NonV2Request_ReturnsNone()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Responses,
            Channel("https://api.openai.com/v1"),
            Payload());

        Assert.Equal(MultiAgentV2Action.None, action);
    }

    [Fact]
    public void Resolve_OfficialResponsesV2_ReturnsPassthrough()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Responses,
            Channel("https://api.openai.com/v1"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Passthrough, action);
    }

    [Fact]
    public void Resolve_ThirdPartyResponsesV2_ReturnsReject()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Responses,
            Channel("https://example.com/v1"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Reject, action);
    }

    [Fact]
    public void Resolve_ChatV2_ReturnsDowngrade()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            Channel("https://api.openai.com/v1"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Downgrade, action);
    }

    [Fact]
    public void Resolve_MessagesV2_ReturnsDowngrade()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            Channel("https://example.com/v1"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Downgrade, action);
    }

    [Fact]
    public void Resolve_ConfiguredRejectOnChat_OverridesDefaultDowngrade()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            Channel("https://example.com/v1", "reject"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Reject, action);
    }

    [Fact]
    public void Resolve_ConfiguredDowngrade_OverridesDefaultReject()
    {
        var action = MultiAgentV2Policy.Resolve(
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            Channel("https://example.com/v1", "downgrade"),
            V2Payload());

        Assert.Equal(MultiAgentV2Action.Downgrade, action);
    }

    [Theory]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, "https://api.openai.com/v1", null, true)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, "https://chatgpt.com/backend-api/codex", null, true)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, "https://example.com/v1", null, false)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, "https://example.com/v1", "passthrough", true)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, "https://example.com/v1", "reject", false)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Chat, "https://api.openai.com/v1", null, false)]
    [InlineData(ProtocolConverter.Chat, ProtocolConverter.Responses, "https://api.openai.com/v1", null, false)]
    public void IsNativeResponsesPassthrough_ResolvesByProtocolAndChannelConfig(
        string entryProtocol,
        string channelType,
        string baseUrl,
        string? configuredMode,
        bool expected)
    {
        var result = MultiAgentV2Policy.IsNativeResponsesPassthrough(
            entryProtocol,
            channelType,
            Channel(baseUrl, configuredMode));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsV2Request_DetectsAgentMessageWithoutTopLevelConfiguration()
    {
        var payload = Payload();
        payload["input"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "agent_message"
            }
        };

        Assert.True(MultiAgentV2Policy.IsV2Request(payload));
    }

    [Fact]
    public void IsV2Request_DisabledTopLevelConfiguration_ReturnsFalse()
    {
        var payload = Payload();
        payload["multi_agent"] = new Dictionary<string, object?>
        {
            ["enabled"] = false
        };

        Assert.False(MultiAgentV2Policy.IsV2Request(payload));
    }

    private static Dictionary<string, object?> V2Payload()
    {
        var payload = Payload();
        payload["multi_agent"] = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["max_concurrent_subagents"] = 3
        };
        return payload;
    }

    private static Dictionary<string, object?> Payload()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "gpt-5.6-terra",
            ["input"] = "ping"
        };
    }

    private static Dictionary<string, object?> Channel(string baseUrl, string? configuredMode = null)
    {
        var channel = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = ProtocolConverter.Responses,
            ["baseurl"] = baseUrl
        };
        if (configuredMode is not null)
        {
            channel["compat"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [MultiAgentV2Policy.CompatKey] = configuredMode
            };
        }

        return channel;
    }
}
