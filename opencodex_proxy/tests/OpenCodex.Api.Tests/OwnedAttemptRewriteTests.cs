using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class OwnedAttemptRewriteTests
{
    [Theory]
    [InlineData(ProtocolConverter.Responses)]
    [InlineData(ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Messages)]
    public void OwnedConversion_MatchesPublicConversionAndNeverTouchesClientGraph(string target)
    {
        var client = Request();
        var original = JsonSerializer.Serialize(client);
        var expected = ProtocolConverter.ConvertRequest(client, ProtocolConverter.Responses, target, "upstream");
        var owned = WebSearchRequestPolicy.ApplyMode(client, "convert");
        var converted = ProtocolConverter.ConvertOwnedRequestWithMetadata(owned, ProtocolConverter.Responses, target, "upstream");
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(converted.Request));
        Assert.Equal("public", owned["model"]);
        converted.Request["model"] = "mutated";
        // Even for same-protocol conversion, tool containers can be reused ONLY within the attempt.
        if (target == ProtocolConverter.Responses)
        {
            var outputTool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(converted.Request["tools"])));
            outputTool["description"] = "changed inside this attempt";
        }
        Assert.Equal(original, JsonSerializer.Serialize(client));
        var retry = WebSearchRequestPolicy.ApplyMode(client, "convert");
        Assert.Equal(original, JsonSerializer.Serialize(retry));
    }

    [Fact]
    public void OwnedCompat_ReusesGraphButPublicEntryRemainsIndependent()
    {
        var client = Request();
        var original = JsonSerializer.Serialize(client);
        var compat = new Dictionary<string, object?>
        {
            ["default_params"] = new Dictionary<string, object?> { ["temperature"] = 0.1 },
            ["rename_params"] = new Dictionary<string, object?> { ["input"] = "renamed_input" }
        };
        var expected = ChannelCompatRequestRewriter.Apply(client, compat);
        var owned = WebSearchRequestPolicy.ApplyMode(client, "convert");
        var result = ChannelCompatRequestRewriter.ApplyOwned(owned, compat);
        Assert.Same(owned, result.Payload);
        Assert.Equal(JsonSerializer.Serialize(expected.Payload), JsonSerializer.Serialize(result.Payload));
        Assert.Equal(expected.Details, result.Details);
        Assert.Equal(original, JsonSerializer.Serialize(client));
    }

    [Fact]
    public void OwnedDowngrade_ReusesAttemptGraphAndKeepsClientHistoryIntact()
    {
        var client = Request();
        client["multi_agent"] = new Dictionary<string, object?> { ["enabled"] = true };
        var original = JsonSerializer.Serialize(client);
        var expected = MultiAgentV2RequestRewriter.Apply(client, MultiAgentV2Action.Downgrade);
        var owned = WebSearchRequestPolicy.ApplyMode(client, "convert");
        var result = MultiAgentV2RequestRewriter.ApplyOwned(owned, MultiAgentV2Action.Downgrade);
        Assert.Same(owned, result);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(result));
        Assert.Equal(original, JsonSerializer.Serialize(client));
    }

    private static Dictionary<string, object?> Request() => new()
    {
        ["model"] = "public", ["input"] = "hello",
        ["tools"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "function", ["name"] = "a__b", ["description"] = "original",
                ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" }
            }
        }
    };
}
