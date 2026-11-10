using System.Text.Json;
using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ToolWireNameIdentityTests
{
    public static IEnumerable<object?[]> ExactIdentities()
    {
        foreach (var protocol in new[] { ProtocolConverter.Chat, ProtocolConverter.Messages })
        foreach (var toolType in new[] { "function", "custom" })
        foreach (var namespaceName in new string?[] { null, "a" })
        foreach (var includeAliasOwner in new[] { false, true })
            yield return [protocol, toolType, namespaceName, includeAliasOwner];
    }

    [Theory]
    [MemberData(nameof(ExactIdentities))]
    public void ToolChoice_PrefersExactDeclaredIdentityOverSeparatorAlias(
        string protocol, string toolType, string? namespaceName, bool includeAliasOwner)
    {
        var request = Request(toolType, namespaceName, includeAliasOwner);
        var choice = new Dictionary<string, object?> { ["type"] = toolType, ["name"] = "a__b" };
        if (namespaceName is not null) choice["namespace"] = namespaceName;
        request["tool_choice"] = choice;

        var converted = ProtocolConverter.ConvertRequest(request, ProtocolConverter.Responses, protocol, "upstream");
        var convertedChoice = Object(converted["tool_choice"]);
        Assert.Equal("a__b", protocol == ProtocolConverter.Chat
            ? Object(convertedChoice["function"])["name"]
            : convertedChoice["name"]);
        Assert.Contains("a__b", DeclaredWireNames(converted, protocol));
    }

    [Theory]
    [MemberData(nameof(ExactIdentities))]
    public void ToolHistory_PrefersExactIdentityAndKeepsResultsAttachedToOriginalCalls(
        string protocol, string toolType, string? namespaceName, bool includeAliasOwner)
    {
        var request = Request(toolType, namespaceName, includeAliasOwner);
        var input = new List<object?>();
        AddHistory(input, toolType, "original-call", "a__b", namespaceName, "original-result");
        if (includeAliasOwner) AddHistory(input, toolType, "alias-call", "b", "a", "alias-result");
        request["input"] = input;

        var converted = ProtocolConverter.ConvertRequest(request, ProtocolConverter.Responses, protocol, "upstream");
        var messages = List(converted, "messages").Select(Object).ToList();
        var calls = protocol == ProtocolConverter.Chat
            ? messages.SelectMany(message => List(message, "tool_calls")).Select(Object).ToList()
            : messages.SelectMany(message => List(message, "content")).Select(Object)
                .Where(block => block["type"]?.ToString() == "tool_use").ToList();
        var results = protocol == ProtocolConverter.Chat
            ? messages.Where(message => message["role"]?.ToString() == "tool").ToList()
            : messages.SelectMany(message => List(message, "content")).Select(Object)
                .Where(block => block["type"]?.ToString() == "tool_result").ToList();
        var expectedNames = includeAliasOwner ? new[] { "a__b", "b" } : new[] { "a__b" };
        Assert.Equal(expectedNames, calls.Select(call => protocol == ProtocolConverter.Chat
            ? Object(call["function"])["name"]?.ToString()
            : call["name"]?.ToString()));
        Assert.Equal(includeAliasOwner ? 2 : 1, results.Count);
        for (var index = 0; index < calls.Count; index++)
        {
            var id = index == 0 ? "original-call" : "alias-call";
            Assert.Equal(id, calls[index]["id"]);
            Assert.Equal(id, results[index][protocol == ProtocolConverter.Chat ? "tool_call_id" : "tool_use_id"]);
            Assert.Equal(index == 0 ? "original-result" : "alias-result", results[index]["content"]);
            Assert.Contains(expectedNames[index], DeclaredWireNames(converted, protocol));
            var arguments = protocol == ProtocolConverter.Chat
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(
                    Object(calls[index]["function"])["arguments"]!.ToString()!)!
                : Object(calls[index]["input"]).ToDictionary(pair => pair.Key, pair => pair.Value!.ToString()!);
            Assert.Equal(toolType == "custom" ? "const result = 1;" : "original-argument",
                arguments[toolType == "custom" ? "input" : "value"]);
        }
    }

    [Theory]
    [InlineData(ProtocolConverter.Chat, null)]
    [InlineData(ProtocolConverter.Messages, null)]
    [InlineData(ProtocolConverter.Chat, "a")]
    [InlineData(ProtocolConverter.Messages, "a")]
    public void UndeclaredSeparatorAlias_RemainsCompatibleForChoiceAndHistory(string protocol, string? namespaceName)
    {
        var request = new Dictionary<string, object?>
        {
            ["tools"] = new List<object?> { Namespace("a", Tool("function", "b")) },
            ["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "function", ["name"] = "a__b", ["namespace"] = namespaceName
            }
        };
        var input = new List<object?>();
        AddHistory(input, "function", "alias-call", "a__b", namespaceName, "alias-result");
        request["input"] = input;

        var converted = ProtocolConverter.ConvertRequest(request, ProtocolConverter.Responses, protocol, "upstream");
        var choice = Object(converted["tool_choice"]);
        Assert.Equal("b", protocol == ProtocolConverter.Chat ? Object(choice["function"])["name"] : choice["name"]);
        var assistant = List(converted, "messages").Select(Object).First(message => message["role"]?.ToString() == "assistant");
        var call = Object(Assert.Single(List(assistant, protocol == ProtocolConverter.Chat ? "tool_calls" : "content")));
        Assert.Equal("b", protocol == ProtocolConverter.Chat ? Object(call["function"])["name"] : call["name"]);
    }

    private static Dictionary<string, object?> Request(string toolType, string? namespaceName, bool includeAliasOwner)
    {
        var tool = Tool(toolType, "a__b");
        var tools = new List<object?> { namespaceName is null ? tool : Namespace(namespaceName, tool) };
        if (includeAliasOwner) tools.Add(Namespace("a", Tool(toolType, "b")));
        return new Dictionary<string, object?> { ["model"] = "public", ["input"] = "hello", ["tools"] = tools };
    }

    private static Dictionary<string, object?> Tool(string type, string name)
    {
        var tool = new Dictionary<string, object?> { ["type"] = type, ["name"] = name };
        tool[type == "custom" ? "format" : "parameters"] = new Dictionary<string, object?>
        {
            ["type"] = type == "custom" ? "text" : "object"
        };
        return tool;
    }

    private static Dictionary<string, object?> Namespace(string name, Dictionary<string, object?> tool) => new()
    {
        ["type"] = "namespace", ["name"] = name, ["tools"] = new List<object?> { tool }
    };

    private static void AddHistory(List<object?> input, string toolType, string id, string name, string? namespaceName, string result)
    {
        var custom = toolType == "custom";
        var call = new Dictionary<string, object?>
        {
            ["type"] = custom ? "custom_tool_call" : "function_call", ["call_id"] = id, ["name"] = name,
            [custom ? "input" : "arguments"] = custom ? "const result = 1;" : "{\"value\":\"original-argument\"}"
        };
        if (namespaceName is not null) call["namespace"] = namespaceName;
        input.Add(call);
        input.Add(new Dictionary<string, object?>
        {
            ["type"] = custom ? "custom_tool_call_output" : "function_call_output", ["call_id"] = id, ["output"] = result
        });
    }

    private static IEnumerable<string?> DeclaredWireNames(Dictionary<string, object?> request, string protocol) =>
        List(request, "tools").Select(Object).Select(tool => protocol == ProtocolConverter.Chat
            ? Object(tool["function"])["name"]?.ToString() : tool["name"]?.ToString());
    private static Dictionary<string, object?> Object(object? value) => Assert.IsType<Dictionary<string, object?>>(value);
    private static List<object?> List(Dictionary<string, object?> value, string key) =>
        value.TryGetValue(key, out var list) ? Assert.IsType<List<object?>>(list) : [];
}
