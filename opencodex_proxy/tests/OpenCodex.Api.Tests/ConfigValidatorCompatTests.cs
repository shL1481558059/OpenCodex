using OpenCodex.Core.Config;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ConfigValidatorCompatTests
{
    [Theory]
    [InlineData("downgrade")]
    [InlineData("passthrough")]
    [InlineData("reject")]
    [InlineData("Downgrade")]
    public void ValidateChannel_MultiAgentV2Mode_IsAccepted(string mode)
    {
        var channel = ChannelWithCompat(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["multi_agent_v2_mode"] = mode
        });

        var validated = ConfigValidator.ValidateChannel(channel);

        var compat = Assert.IsType<Dictionary<string, object?>>(validated["compat"]);
        Assert.Equal(mode, compat["multi_agent_v2_mode"]);
    }

    [Fact]
    public void ValidateChannel_UnknownMultiAgentV2Mode_IsRejected()
    {
        var channel = ChannelWithCompat(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["multi_agent_v2_mode"] = "unsupported"
        });

        var error = Assert.Throws<ConfigException>(() => ConfigValidator.ValidateChannel(channel));

        Assert.Contains("multi_agent_v2_mode", error.Message);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("context_pool")]
    [InlineData("passthrough")]
    [InlineData("off")]
    [InlineData("CONTEXT_POOL")]
    public void ValidateChannel_ResponsesWsMode_IsAccepted(string mode)
    {
        var channel = ChannelWithCompat(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ws_mode"] = mode },
            type: "responses");

        var validated = ConfigValidator.ValidateChannel(channel);

        var compat = Assert.IsType<Dictionary<string, object?>>(validated["compat"]);
        Assert.Equal(mode.ToLowerInvariant(), compat["ws_mode"]);
    }

    [Fact]
    public void ValidateChannel_UnknownResponsesWsMode_IsRejected()
    {
        var channel = ChannelWithCompat(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ws_mode"] = "unsupported" },
            type: "responses");

        var error = Assert.Throws<ConfigException>(() => ConfigValidator.ValidateChannel(channel));

        Assert.Contains("ws_mode", error.Message);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("context_pool")]
    [InlineData("passthrough")]
    [InlineData("off")]
    public void ValidateChannel_UpstreamWsPoolingRequiresResponsesChannel(string mode)
    {
        var channel = ChannelWithCompat(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ws_mode"] = mode },
            type: "chat");

        var error = Assert.Throws<ConfigException>(() => ConfigValidator.ValidateChannel(channel));

        Assert.Contains("only supported for responses channels", error.Message);
    }

    private static Dictionary<string, object?> ChannelWithCompat(
        Dictionary<string, object?> compat,
        string type = "chat")
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "channel-1",
            ["type"] = type,
            ["baseurl"] = "https://example.com/v1",
            ["capacity"] = 10,
            ["compat"] = compat,
            ["models"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["model"] = "deepseek-v4.1-flash",
                    ["upstream_model"] = "cline-pass/deepseek-v4.1-flash"
                }
            }
        };
    }
}
