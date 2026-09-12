using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchRequestPolicyTests
{
    [Theory]
    [InlineData("web_search", "chat")]
    [InlineData("web_search_preview", "messages")]
    public void RegisterBuiltin_ReplacesOnlyNativeSearchAndMapsChoice(string nativeType, string protocol)
    {
        var payload = Request(nativeType);
        payload["tool_choice"] = new Dictionary<string, object?> { ["type"] = nativeType };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", protocol, "superadmin");
        Assert.NotNull(binding);
        Assert.True(binding.SearchAllowed);
        var tools = WebSearchPayload.ListValue(payload, "tools").Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("opencodex_web_search", tools[0]["name"]);
        Assert.Equal("web_search", tools[1]["name"]);
        Assert.Equal("opencodex_web_search", WebSearchPayload.ObjectValue(payload, "tool_choice")["name"]);
        var converted = ProtocolConverter.ConvertRequest(payload, "responses", protocol, "upstream");
        WebSearchRequestPolicy.FinalizeUpstreamRequest(converted, binding);
        Assert.Equal(2, WebSearchPayload.ListValue(converted, "tools").Count);
    }

    [Theory]
    [InlineData("convert", "responses", "chat", "superadmin")]
    [InlineData("simulate", "chat", "chat", "superadmin")]
    [InlineData("simulate", "responses", "responses", "superadmin")]
    [InlineData("simulate", "responses", "messages", "user")]
    public void IneligibleRequest_DoesNotRegister(string mode, string entry, string channel, string role)
    {
        var payload = Request();
        Assert.Null(WebSearchRequestPolicy.RegisterBuiltin(payload, mode, entry, channel, role));
        Assert.Equal("web_search", WebSearchPayload.ObjectValue(
            new Dictionary<string, object?> { ["tool"] = WebSearchPayload.ListValue(payload, "tools")[0] }, "tool")["type"]);
    }

    [Fact]
    public void NoNativeDeclaration_DoesNotInjectSearch()
    {
        var payload = new Dictionary<string, object?> { ["input"] = "Hello" };
        Assert.Null(WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
        Assert.False(payload.ContainsKey("tools"));
    }

    [Fact]
    public void Collision_UsesDeterministicDistinctName()
    {
        var payload = Request();
        WebSearchPayload.ListValue(payload, "tools").Add(new Dictionary<string, object?>
        {
            ["type"] = "function", ["name"] = WebSearchRequestPolicy.InternalToolName
        });
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");
        Assert.Equal("opencodex_web_search_2", binding!.WebSearchToolName);
    }

    [Fact]
    public void None_KeepsChoiceAndDisallowsExecution()
    {
        var payload = Request();
        payload["tool_choice"] = "none";
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");
        Assert.False(binding!.SearchAllowed);
        Assert.Equal("none", payload["tool_choice"]);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public void AllowedTools_DoesNotBroadenTheUpstreamToolSet(string protocol)
    {
        var payload = Request();
        payload["tool_choice"] = new Dictionary<string, object?>
        {
            ["type"] = "allowed_tools",
            ["mode"] = "required",
            ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } }
        };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", protocol, "superadmin");
        var upstream = ProtocolConverter.ConvertRequest(payload, "responses", protocol, "model");
        WebSearchRequestPolicy.FinalizeUpstreamRequest(upstream, binding);
        Assert.Single(WebSearchPayload.ListValue(upstream, "tools"));
        Assert.True(binding!.SearchAllowed);
    }

    [Theory]
    [InlineData("{\"query\":123}")]
    [InlineData("{\"query\":null}")]
    [InlineData("{\"query\":\"a\",\"query\":\"b\"}")]
    public void InvalidQuery_DoesNotCoerceOrAcceptDuplicateKeys(string arguments)
    {
        Assert.NotNull(WebSearchRequestPolicy.ParseQuery(arguments).Error);
    }

    [Fact]
    public void UnsupportedOfflineSearch_IsRejectedBeforeModelRequest()
    {
        var payload = Request();
        ((Dictionary<string, object?>)WebSearchPayload.ListValue(payload, "tools")[0]!)["external_web_access"] = false;
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Fact]
    public void ZeroBudget_RejectsForcedSearch()
    {
        var payload = Request();
        payload["max_tool_calls"] = 0;
        payload["tool_choice"] = new Dictionary<string, object?> { ["type"] = "web_search" };
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Fact]
    public void ZeroBudget_RejectsRequiredAllowListContainingOnlySearch()
    {
        var payload = Request();
        payload["max_tool_calls"] = 0;
        payload["tool_choice"] = new Dictionary<string, object?>
        {
            ["type"] = "allowed_tools",
            ["mode"] = "required",
            ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } }
        };
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "messages", "superadmin"));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public void RequiredAllowList_RejectsAnEmptyEffectiveToolSet(string protocol)
    {
        var payload = Request();
        payload["tool_choice"] = new Dictionary<string, object?>
        {
            ["type"] = "allowed_tools",
            ["mode"] = "required",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "function", ["name"] = "unavailable_tool" }
            }
        };
        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", protocol, "superadmin");
        var request = ProtocolConverter.ConvertRequest(payload, "responses", protocol, "model");

        Assert.Throws<BadRequestException>(() => WebSearchRequestPolicy.FinalizeUpstreamRequest(request, binding));
    }

    private static Dictionary<string, object?> Request(string type = "web_search") => new()
    {
        ["input"] = "Hello",
        ["tools"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = type },
            new Dictionary<string, object?>
            {
                ["type"] = "function", ["name"] = "web_search",
                ["parameters"] = new Dictionary<string, object?>()
            }
        }
    };
}
