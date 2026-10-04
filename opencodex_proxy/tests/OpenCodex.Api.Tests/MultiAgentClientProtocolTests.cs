using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentClientProtocolTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/root")]
    public void Identity_RootIgnoresSessionDifferencesBecauseThreadOwnsItsIdentity(string agent)
    {
        var identity = MultiAgentClientIdentity.Parse(new Dictionary<string, string>
        {
            ["thread-id"] = "sidebar-thread",
            ["session-id"] = "main-session",
            ["x-codex-turn-metadata"] = JsonSerializer.Serialize(new
            {
                thread_id = "sidebar-thread", session_id = "sidebar-session",
                agent_name = agent, turn_id = "sidebar-turn"
            })
        });

        Assert.NotNull(identity);
        Assert.Equal("sidebar-thread", identity.ThreadId);
        Assert.Equal("sidebar-thread", identity.RootThreadId);
        Assert.Equal("/root", identity.AgentName);
        Assert.Equal("", identity.ParentThreadId);
        Assert.Equal("sidebar-turn", identity.TurnId);
    }

    [Fact]
    public void Identity_UnboundRequestIgnoresSessionDifferences()
    {
        Assert.Null(MultiAgentClientIdentity.Parse(new Dictionary<string, string>
        {
            ["session-id"] = "main-session",
            ["x-codex-turn-metadata"] = """{"session_id":"sidebar-session"}"""
        }));
    }

    [Fact]
    public void Identity_OrdinaryRequestsWithoutThreadIdentityRemainUnbound()
    {
        Assert.Null(MultiAgentClientIdentity.Parse(new Dictionary<string, string> { ["session-id"] = "legacy" }));
    }

    [Fact]
    public void Identity_RootUsesActualThreadAndChildUsesExplicitRootSession()
    {
        var root = MultiAgentClientIdentity.Parse(new Dictionary<string, string>
        {
            ["Thread-Id"] = "root-thread", ["session-id"] = "different-session",
            ["x-codex-turn-metadata"] = """{"thread_id":"root-thread","agent_name":"/root","turn_id":"turn-1"}"""
        })!;
        Assert.Equal("root-thread", root.ThreadId);
        Assert.Equal("root-thread", root.RootThreadId);
        Assert.Equal("", root.ParentThreadId);
        Assert.Equal("/root", root.AgentName);
        Assert.Equal("turn-1", root.TurnId);

        var child = MultiAgentClientIdentity.Parse(ChildHeaders())!;
        Assert.Equal("child-thread", child.ThreadId);
        Assert.Equal("root-thread", child.RootThreadId);
        Assert.Equal("parent-thread", child.ParentThreadId);
        Assert.Equal("/root/parent/child", child.AgentName);
    }

    [Theory]
    [InlineData("thread-id", "conflicting")]
    [InlineData("x-codex-parent-thread-id", "conflicting")]
    [InlineData("session-id", "conflicting")]
    public void Identity_RejectsConflictingMetadataAndHeaders(string header, string value)
    {
        var headers = ChildHeaders();
        headers[header] = value;
        Assert.Throws<BadRequestException>(() => MultiAgentClientIdentity.Parse(headers));
    }

    [Fact]
    public void Identity_RejectsCaseInsensitiveHeaderConflictsAndDuplicateMetadataFields()
    {
        var headers = ChildHeaders();
        headers["Thread-Id"] = "different";
        Assert.Throws<BadRequestException>(() => MultiAgentClientIdentity.Parse(headers));
        headers = new() { ["x-codex-turn-metadata"] = """{"thread_id":"first","thread_id":"second"}""" };
        Assert.Throws<BadRequestException>(() => MultiAgentClientIdentity.Parse(headers));
    }

    [Theory]
    [InlineData("""{"thread_id":"child","parent_thread_id":"parent","session_id":"root"}""")]
    [InlineData("""{"thread_id":"child","parent_thread_id":"parent","agent_name":"/root/child"}""")]
    [InlineData("""{"thread_id":"child","agent_name":"/root/child","subagent_kind":"thread_spawn"}""")]
    [InlineData("""{"thread_id":42}""")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void Identity_RejectsIncompleteOrMalformedChildIdentity(string metadata)
    {
        Assert.Throws<BadRequestException>(() => MultiAgentClientIdentity.Parse(new Dictionary<string, string>
        {
            ["x-codex-turn-metadata"] = metadata
        }));
    }

    [Fact]
    public void Tools_RestoreActualNativeDefinitionsAfterLegacyNormalization()
    {
        var request = Request();
        var tools = MultiAgentClientTools.FromRequest(request);
        Assert.True(tools.CanSpawn);
        var template = tools.ApplyToTemplate(MultiAgentProtocol.NormalizeRequest(request));
        var definitions = JsonDictionaryValue.List(template, "tools");
        Assert.Equal(2, definitions.Count);
        Assert.Contains(definitions.OfType<Dictionary<string, object?>>(), tool => Equals(tool["name"], "exec"));
        Assert.DoesNotContain(definitions.OfType<Dictionary<string, object?>>(), tool =>
            JsonDictionaryValue.String(tool, "name").StartsWith("ocxp_ma_", StringComparison.Ordinal));
        Assert.Equal(WebSearchPayload.JsonDumps(JsonDictionaryValue.List(request, "tools")[1]),
            WebSearchPayload.JsonDumps(definitions[1]));
        Assert.Equal("spawn_agent", tools.Action(Call("{}")));
        Assert.Null(tools.Action(Payload("""{"type":"function_call","name":"spawn_agent","namespace":"different"}""")));
        Assert.Null(tools.Action(Payload("""{"type":"function_call","name":"collaboration.spawn_agent"}""")));
    }

    [Theory]
    [InlineData("collaboration.spawn_agent")]
    [InlineData("collaboration_spawn_agent")]
    [InlineData("collaboration__spawn_agent")]
    public void Tools_RecognizeOnlyActuallyDeclaredFlatNames(string name)
    {
        var request = Payload("""{"tools":[{"type":"function","name":"placeholder","parameters":{"type":"object","properties":{}}}]}""");
        ((Dictionary<string, object?>)JsonDictionaryValue.List(request, "tools")[0]!)["name"] = name;
        var tools = MultiAgentClientTools.FromRequest(request);
        Assert.True(tools.CanSpawn);
        Assert.Equal("spawn_agent", tools.Action(Payload($$"""{"type":"function_call","name":"{{name}}","arguments":"{}"}""")));
        Assert.Null(tools.Action(Call("{}")));
    }

    [Theory]
    [InlineData("collaboration.spawn_agent")]
    [InlineData("collaboration__spawn_agent")]
    [InlineData("collaboration_spawn_agent")]
    public void FlatCollaborationCallRetainsDeclaredIdentityAfterChatConversion(string name)
    {
        var request = Payload("""{"model":"test","tools":[{"type":"function","name":"placeholder","parameters":{"type":"object","properties":{}}}]}""");
        ((Dictionary<string, object?>)JsonDictionaryValue.List(request, "tools")[0]!)["name"] = name;
        var tools = MultiAgentClientTools.FromRequest(request);
        var chat = ProtocolConverter.ConvertRequest(request, ProtocolConverter.Responses, ProtocolConverter.Chat, "test");
        var chatTool = (Dictionary<string, object?>)JsonDictionaryValue.List(chat, "tools").Single()!;
        var upstreamName = ((Dictionary<string, object?>)chatTool["function"]!)["name"];
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var started = ProtocolConverter.ResponsesToolCallStartedItem("call", upstreamName, "item", mappings);
        var completed = ProtocolConverter.ResponsesToolCallItemFromToolCall("call", upstreamName, new Dictionary<string, object?>(), mappings: mappings);
        foreach (var call in new[] { started, completed })
        {
            Assert.Equal(name, call["name"]);
            Assert.False(call.ContainsKey("namespace"));
            Assert.Equal("spawn_agent", tools.Action(call));
        }
    }

    [Fact]
    public void DeclaredNamespaceCallRetainsNativeIdentityAfterChatConversion()
    {
        var request = Request();
        var tools = MultiAgentClientTools.FromRequest(request);
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var call = ProtocolConverter.ResponsesToolCallItemFromToolCall("call", "collaboration__spawn_agent", new Dictionary<string, object?>(), mappings: mappings);
        Assert.Equal("spawn_agent", call["name"]);
        Assert.Equal("collaboration", call["namespace"]);
        Assert.Equal("spawn_agent", tools.Action(call));
    }

    [Fact]
    public void NestedNamespaceMappingPreservesLeafNameContainingSeparators()
    {
        var request = Payload("""{"tools":[{"type":"namespace","name":"outer","tools":[{"type":"namespace","name":"inner","tools":[{"type":"function","name":"read__file","parameters":{"type":"object","properties":{}}}]}]}]}""");
        var mappings = ProtocolConverter.BuildResponsesToolCallMappings(request);
        var call = ProtocolConverter.ResponsesToolCallItemFromToolCall("call", "outer__inner__read__file", new Dictionary<string, object?>(), mappings: mappings);
        Assert.Equal("read__file", call["name"]);
        Assert.Equal("outer__inner", call["namespace"]);
    }

    [Fact]
    public void Tools_AdditionalDefinitionsMergeAndRoundTripWithoutSharedState()
    {
        var request = Request();
        request["input"] = JsonDictionaryValue.List(Payload("""{"input":[{"type":"additional_tools","tools":[{"type":"namespace","name":"collaboration","description":"native","tools":[{"type":"function","name":"wait_agent","parameters":{"type":"object","properties":{"timeout_ms":{"type":"integer"}}}}]}]}]}"""), "input");
        var tools = MultiAgentClientTools.FromRequest(request);
        var restored = MultiAgentClientTools.FromDefinitions(tools.Definitions);
        Assert.True(restored.CanSpawn);
        Assert.Equal("wait_agent", restored.Action(Payload("""{"name":"wait_agent","namespace":"collaboration"}""")));
        ((Dictionary<string, object?>)tools.Definitions[0]!)["description"] = "changed";
        Assert.Equal("native", ((Dictionary<string, object?>)restored.Definitions[0]!)["description"]);
    }

    [Fact]
    public void Arguments_PreserveExplicitModelAndReasoningOnlyWhenDeclared()
    {
        var tools = MultiAgentClientTools.FromRequest(Request());
        var arguments = tools.Arguments(Call("""{"task_name":"audit","message":" preserve\ntext ","model":"gpt-6-luna","reasoning_effort":"high"}"""));
        Assert.Equal("gpt-6-luna", arguments["model"]);
        Assert.Equal("high", arguments["reasoning_effort"]);
        Assert.Equal(" preserve\ntext ", arguments["message"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"task_name":"a","message":"b","model":42}""")]
    [InlineData("""{"task_name":"a","message":"b","reasoning_effort":"ultra"}""")]
    [InlineData("""{"task_name":"a","message":"b","unknown":"secret-value"}""")]
    [InlineData("""{"task_name":"a","message":"b","message":"duplicate"}""")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void Arguments_RejectInvalidValuesWithoutEchoingPayload(string arguments)
    {
        var tools = MultiAgentClientTools.FromRequest(Request());
        var error = Assert.Throws<BadRequestException>(() => tools.Arguments(Call(arguments)));
        Assert.DoesNotContain("secret-value", error.Message);
    }

    [Fact]
    public void Arguments_DoNotSilentlyAcceptModelIfClientSchemaDoesNotDeclareIt()
    {
        var request = Request();
        var ns = (Dictionary<string, object?>)JsonDictionaryValue.List(request, "tools")[1]!;
        var definition = (Dictionary<string, object?>)JsonDictionaryValue.List(ns, "tools")[0]!;
        var schema = (Dictionary<string, object?>)definition["parameters"]!;
        ((Dictionary<string, object?>)schema["properties"]!).Remove("model");
        Assert.Throws<BadRequestException>(() => MultiAgentClientTools.FromRequest(request)
            .Arguments(Call("""{"task_name":"a","message":"b","model":"gpt-6-luna"}""")));
    }

    private static Dictionary<string, string> ChildHeaders() => new()
    {
        ["thread-id"] = "child-thread", ["x-codex-parent-thread-id"] = "parent-thread", ["session-id"] = "root-thread",
        ["x-codex-turn-metadata"] = """{"thread_id":"child-thread","parent_thread_id":"parent-thread","session_id":"root-thread","agent_name":"/root/parent/child","subagent_kind":"thread_spawn","turn_id":"turn-child"}"""
    };

    private static Dictionary<string, object?> Request() => Payload("""
        {"tools":[{"type":"custom","name":"exec","format":{"type":"text"}},
          {"type":"namespace","name":"collaboration","description":"native","tools":[
            {"type":"function","name":"spawn_agent","strict":false,"description":"original","parameters":{
              "type":"object","properties":{"task_name":{"type":"string"},"message":{"type":"string"},
                "model":{"type":"string"},"reasoning_effort":{"type":"string","enum":["low","high"]}},
              "required":["task_name","message"],"additionalProperties":false}}]}]}
        """);

    private static Dictionary<string, object?> Call(string arguments) => new()
    {
        ["type"] = "function_call", ["name"] = "spawn_agent", ["namespace"] = "collaboration", ["arguments"] = arguments
    };

    private static Dictionary<string, object?> Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)!;
    }
}
