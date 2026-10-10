using System.Text.Json;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalWebAccess_BooleanDoesNotBlockProxySearch(bool externalWebAccess)
    {
        var payload = Request();
        NativeTool(payload)["external_web_access"] = externalWebAccess;

        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");

        Assert.NotNull(binding);
        Assert.True(binding.SearchAllowed);
        Assert.Equal("opencodex_web_search", NativeTool(payload)["name"]);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void ExternalWebAccess_JsonElementBooleanDoesNotBlockProxySearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var payload = Request();
        NativeTool(payload)["external_web_access"] = document.RootElement.Clone();

        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");

        Assert.NotNull(binding);
        Assert.True(binding.SearchAllowed);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void ExternalWebAccess_NonBooleanIsRejected(object value)
    {
        var payload = Request();
        NativeTool(payload)["external_web_access"] = value;
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Fact]
    public void ExternalWebAccess_JsonElementStringIsRejected()
    {
        using var document = JsonDocument.Parse("\"false\"");
        var payload = Request();
        NativeTool(payload)["external_web_access"] = document.RootElement.Clone();
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Fact]
    public void SupportedNativeDefaults_StillPass()
    {
        var payload = Request();
        var tool = NativeTool(payload);
        tool["description"] = "search";
        tool["external_web_access"] = true;
        tool["search_context_size"] = "medium";
        tool["return_token_budget"] = "default";
        tool["search_content_types"] = new List<object?> { "text" };

        Assert.NotNull(WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void SearchContextSize_IsStoredOnTheBinding(string size)
    {
        var payload = Request();
        NativeTool(payload)["search_context_size"] = size;

        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");

        Assert.Equal(size, binding!.WebSearch.ContextSize);
        Assert.Empty(binding.WebSearch.AllowedDomains);
    }

    [Fact]
    public void OmittedSearchOptions_UseMediumWithoutDomains()
    {
        var binding = WebSearchRequestPolicy.RegisterBuiltin(Request(), "simulate", "responses", "chat", "superadmin");

        Assert.Equal(WebSearchExecutionOptions.Medium, binding!.WebSearch.ContextSize);
        Assert.Empty(binding.WebSearch.AllowedDomains);
    }

    [Fact]
    public void AllowedDomains_AreNormalizedAndStored()
    {
        using var domain = JsonDocument.Parse("\"Docs.Example.com\"");
        var payload = Request();
        NativeTool(payload)["search_context_size"] = "low";
        NativeTool(payload)["filters"] = new Dictionary<string, object?>
        {
            ["allowed_domains"] = new List<object?>
            {
                " Example.COM. ",
                "example.com",
                domain.RootElement.Clone()
            }
        };

        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");

        Assert.Equal(WebSearchExecutionOptions.Low, binding!.WebSearch.ContextSize);
        Assert.Equal(["example.com", "docs.example.com"], binding.WebSearch.AllowedDomains);
    }

    [Fact]
    public void SearchContextSize_JsonElementIsAccepted()
    {
        using var size = JsonDocument.Parse("\"high\"");
        var payload = Request();
        NativeTool(payload)["search_context_size"] = size.RootElement.Clone();

        var binding = WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin");

        Assert.Equal(WebSearchExecutionOptions.High, binding!.WebSearch.ContextSize);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("LOW")]
    public void UnsupportedContextSize_IsRejected(string size)
    {
        var payload = Request();
        NativeTool(payload)["search_context_size"] = size;
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    [Fact]
    public void AllowedDomains_RejectsEmptyInvalidAndOversizedLists()
    {
        AssertRejectedMessage(
            "filters",
            new Dictionary<string, object?> { ["allowed_domains"] = new List<object?>() },
            "proxy web search allowed_domains must not be empty");
        AssertRejectedMessage(
            "filters",
            new Dictionary<string, object?> { ["allowed_domains"] = new List<object?> { "  " } },
            "proxy web search allowed_domains contains an empty domain");
        AssertRejectedMessage(
            "filters",
            new Dictionary<string, object?> { ["allowed_domains"] = new List<object?> { "https://example.com" } },
            "proxy web search allowed_domains contains an invalid domain");
        AssertRejectedMessage(
            "filters",
            new Dictionary<string, object?>
            {
                ["allowed_domains"] = Enumerable.Range(0, 101).Select(index => (object?)$"d{index}.example").ToList()
            },
            "proxy web search allowed_domains exceeds the limit of 100");
        AssertRejectedMessage(
            "filters",
            new Dictionary<string, object?> { ["blocked_domains"] = new List<object?> { "example.com" } },
            "proxy web search does not support the requested 'filters.blocked_domains' option");
    }

    [Fact]
    public void OptionalCodexSearchConstraints_StayRejected()
    {
        AssertRejected("user_location", new Dictionary<string, object?>
        {
            ["type"] = "approximate",
            ["country"] = "US"
        });
        AssertRejected("search_content_types", new List<object?> { "text", "image" });
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

    private static Dictionary<string, object?> NativeTool(Dictionary<string, object?> payload) =>
        (Dictionary<string, object?>)WebSearchPayload.ListValue(payload, "tools")[0]!;

    private static void AssertRejected(string key, object? value)
    {
        var payload = Request();
        NativeTool(payload)[key] = value;
        Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
    }

    private static void AssertRejectedMessage(string key, object? value, string message)
    {
        var payload = Request();
        NativeTool(payload)[key] = value;
        var error = Assert.Throws<BadRequestException>(() =>
            WebSearchRequestPolicy.RegisterBuiltin(payload, "simulate", "responses", "chat", "superadmin"));
        Assert.Equal(message, error.Message);
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
