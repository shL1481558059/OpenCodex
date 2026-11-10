using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class RequestConversionResultTests
{
    [Theory]
    [InlineData(ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Messages)]
    public void Metadata_IsImmutableAndUsesTheSameExactIdentitiesAsConvertedRequest(string target)
    {
        var leaf = Tool("a__b");
        var request = new Dictionary<string, object?>
        {
            ["model"] = "public", ["input"] = "hello",
            ["tools"] = new List<object?>
            {
                leaf,
                new Dictionary<string, object?>
                {
                    ["type"] = "namespace", ["name"] = "a", ["tools"] = new List<object?> { Tool("b") }
                }
            },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "function", ["name"] = "a__b" }
        };
        var original = JsonSerializer.Serialize(request);
        var attempt = ProtocolConverter.ConvertRequestWithMetadata(request, ProtocolConverter.Responses, target, "upstream");
        var mappings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ResponsesToolCallMapping>>(attempt.ToolCallMappings);
        Assert.Equal("a__b", mappings["a__b"].ResponsesName);
        Assert.Null(mappings["a__b"].Namespace);
        Assert.Equal("a", mappings["b"].Namespace);
        Assert.Equal("b", mappings["b"].ResponsesName);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ResponsesToolCallMapping>)mappings).Clear());
        Assert.Equal(original, JsonSerializer.Serialize(request));

        // Later caller mutations cannot change the contract of an already converted attempt.
        leaf["name"] = "new_name";
        request.Remove("tool_choice");
        var next = ProtocolConverter.ConvertRequestWithMetadata(request, ProtocolConverter.Responses, target, "other-upstream");
        Assert.NotSame(mappings, next.ToolCallMappings);
        Assert.Contains("a__b", mappings.Keys);
        Assert.Contains("new_name", next.ToolCallMappings!.Keys);
        Assert.DoesNotContain("new_name", mappings.Keys);
        Assert.Equal("upstream", attempt.Request["model"]);
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Messages)]
    public void EmptyContract_IsNotNullAndStillRejectsUndeclaredToolCalls(string target)
    {
        var attempt = ProtocolConverter.ConvertRequestWithMetadata(
            new Dictionary<string, object?> { ["input"] = "hello" }, ProtocolConverter.Responses, target, "upstream");
        Assert.NotNull(attempt.ToolCallMappings);
        Assert.Empty(attempt.ToolCallMappings);
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ResolveResponsesToolCallShape("unexpected", attempt.ToolCallMappings));
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Messages)]
    public void MidHistoryDeclarations_AreIncludedInAttemptContract(string target)
    {
        var request = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "additional_tools", ["tools"] = new List<object?> { Tool("loaded_tool") }
                },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = "hello" }
            }
        };
        var attempt = ProtocolConverter.ConvertRequestWithMetadata(request, ProtocolConverter.Responses, target, "upstream");
        Assert.Contains("loaded_tool", attempt.ToolCallMappings!.Keys);
        Assert.Contains("loaded_tool", JsonSerializer.Serialize(attempt.Request));
    }

    [Fact]
    public void SameProtocol_StillReturnsIndependentRequestGraphWithoutCrossProtocolContract()
    {
        var tool = Tool("function");
        var request = new Dictionary<string, object?> { ["tools"] = new List<object?> { tool } };
        var attempt = ProtocolConverter.ConvertRequestWithMetadata(request, ProtocolConverter.Responses, ProtocolConverter.Responses, "upstream");
        Assert.Null(attempt.ToolCallMappings);
        var outputTool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(attempt.Request["tools"])));
        outputTool["name"] = "changed";
        Assert.Equal("function", tool["name"]);
    }

    private static Dictionary<string, object?> Tool(string name) => new()
    {
        ["type"] = "function", ["name"] = name,
        ["parameters"] = new Dictionary<string, object?> { ["type"] = "object" }
    };
}
