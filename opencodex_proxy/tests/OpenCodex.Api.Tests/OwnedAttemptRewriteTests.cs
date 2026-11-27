using System.Text.Json;
using OpenCodex.ProtocolConversion;
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
        if (target == ProtocolConverter.Responses)
        {
            Assert.Same(client["tools"], converted.Request["tools"]);
        }
        Assert.Equal(original, JsonSerializer.Serialize(client));
        var retry = WebSearchRequestPolicy.ApplyMode(client, "convert");
        Assert.Equal(original, JsonSerializer.Serialize(retry));
    }

    [Fact]
    public void ApplyMode_SharesHistoryAndEachRewriterCopiesOnlyTouchedContainers()
    {
        var client = Request();
        client["tools"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "web_search" },
            new Dictionary<string, object?> { ["type"] = "function", ["name"] = "lookup" }
        };
        client["tool_choice"] = new Dictionary<string, object?>
        {
            ["type"] = "allowed_tools",
            ["mode"] = "auto",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "web_search" },
                new Dictionary<string, object?> { ["type"] = "function", ["name"] = "lookup" }
            }
        };
        var original = JsonSerializer.Serialize(client);

        var attempt = WebSearchRequestPolicy.ApplyMode(client, "simulate");
        Assert.NotSame(client, attempt);
        Assert.Same(client["input"], attempt["input"]);
        Assert.Same(client["tools"], attempt["tools"]);
        Assert.Same(client["tool_choice"], attempt["tool_choice"]);

        WebSearchRequestPolicy.RegisterBuiltin(attempt, "simulate", "responses", "chat", "superadmin");

        Assert.NotSame(client["tools"], attempt["tools"]);
        Assert.NotSame(client["tool_choice"], attempt["tool_choice"]);
        Assert.Equal(original, JsonSerializer.Serialize(client));
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

    [Fact]
    public void OwnedResponseConversionReusesNestedGraphWhilePublicConversionStaysIndependent()
    {
        var output = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "hello" }
                }
            }
        };
        var response = new Dictionary<string, object?>
        {
            ["id"] = "resp",
            ["status"] = "completed",
            ["output"] = output
        };

        var owned = ProtocolConverter.ConvertOwnedResponse(
            response, ProtocolConverter.Responses, ProtocolConverter.Responses, "public");
        var independent = ProtocolConverter.ConvertResponse(
            response, ProtocolConverter.Responses, ProtocolConverter.Responses, "public");

        Assert.NotSame(response, owned);
        Assert.Same(output, owned["output"]);
        Assert.NotSame(output, independent["output"]);
        Assert.Equal("completed", response["status"]);
    }

    [Fact]
    public void ChatHistoryNormalizationNeverMutatesBorrowedMessages()
    {
        var reasoning = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = string.Empty,
            ["reasoning_content"] = "reasoning"
        };
        var toolCall = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = "answer",
            ["tool_calls"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "call-1",
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "lookup",
                        ["arguments"] = "{}"
                    }
                }
            }
        };
        var request = new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["messages"] = new List<object?> { reasoning, toolCall }
        };
        var original = JsonSerializer.Serialize(request);

        _ = ProtocolConverter.ConvertOwnedRequestWithMetadata(
            new Dictionary<string, object?>(request, StringComparer.Ordinal),
            ProtocolConverter.Chat,
            ProtocolConverter.Messages,
            "upstream");

        Assert.Equal(original, JsonSerializer.Serialize(request));
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
