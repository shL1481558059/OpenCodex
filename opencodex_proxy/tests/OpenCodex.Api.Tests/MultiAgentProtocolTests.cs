using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentProtocolTests
{
    [Fact]
    public void Normalize_MergesNamespacesAndPreservesCustomDefinitions()
    {
        var request = Payload("""{"model":"m","tools":[{"type":"namespace","name":"files","tools":[{"type":"custom","name":"read","format":{"type":"text"}}]}],"input":[{"type":"additional_tools","tools":[{"type":"namespace","name":"files","tools":[{"type":"function","name":"write"}]}]}],"multi_agent":{},"previous_response_id":"old","conversation":"old","context_management":[]}""");
        var result = MultiAgentProtocol.NormalizeRequest(request);
        var ns = Tools(result).Single(t => Name(t) == "files");
        Assert.Equal(new[] { "read", "write" }, Tools(ns).Select(Name));
        Assert.Equal("text", JsonDictionaryValue.String(JsonDictionaryValue.Object(Tools(ns)[0], "format", WebSearchPayload.DeepCopyObject), "type"));
        Assert.Equal(7, Tools(result).Count);
        foreach (var key in new[] { "input", "multi_agent", "previous_response_id", "conversation", "context_management" }) Assert.False(result.ContainsKey(key));
        Assert.True(request.ContainsKey("input"));
    }

    [Theory]
    [InlineData("ocxp_ma_spawn_agent")]
    [InlineData("ocxp_ma_unknown")]
    public void Normalize_RejectsReservedNamesIncludingNested(string name)
    {
        Assert.Throws<BadRequestException>(() => MultiAgentProtocol.NormalizeRequest(Payload($$"""{"tools":[{"type":"namespace","name":"files","tools":[{"type":"function","name":"{{name}}"}]}]}""")));
    }

    [Fact]
    public void Normalize_ReplacesClientCollaborationToolsWithServerActions()
    {
        var result = MultiAgentProtocol.NormalizeRequest(Payload("""{"tools":[{"type":"namespace","name":"collaboration","tools":[]},{"type":"function","name":"collaboration.spawn_agent"},{"type":"function","name":"collaboration_send_message"},{"type":"multi_agent"},{"type":"function","name":"read"}]}"""));
        Assert.Equal(7, Tools(result).Count);
        Assert.Equal(6, Tools(result).Count(t => MultiAgentProtocol.ActionName(Name(t)) != null));
    }

    [Theory]
    [InlineData("spawn_agent")]
    [InlineData("send_message")]
    [InlineData("followup_task")]
    [InlineData("wait_agent")]
    [InlineData("interrupt_agent")]
    [InlineData("list_agents")]
    public void ActionName_AcceptsOnlyInternalKnownActions(string action)
    {
        Assert.Equal(action, MultiAgentProtocol.ActionName("ocxp_ma_" + action));
        Assert.Null(MultiAgentProtocol.ActionName(action));
        Assert.Null(MultiAgentProtocol.ActionName("collaboration." + action));
        Assert.Null(MultiAgentProtocol.ActionName("ocxp_ma_unknown"));
    }

    [Theory]
    [InlineData("{\"tools\":[42]}")]
    [InlineData("{\"input\":[42]}")]
    public void MalformedItems_AreRejected(string json)
    {
        var request = Payload(json);
        Assert.Throws<BadRequestException>(() => request.ContainsKey("tools") ? MultiAgentProtocol.NormalizeRequest(request) : (object)MultiAgentProtocol.InitialHistory(request));
    }

    [Fact]
    public void InitialHistory_PreservesToolOutputsAndStripsRoutingMetadata()
    {
        var request = Payload("""{"input":[{"type":"additional_tools","tools":[]},{"role":"user","content":"task","agent":"a","additional_tools":[]},{"type":"function_call_output","call_id":"c","output":"result","agent":"a"}]}""");
        var history = MultiAgentProtocol.InitialHistory(request);
        Assert.Equal(2, history.Count);
        Assert.DoesNotContain("agent", ((Dictionary<string, object?>)history[0]!).Keys);
        Assert.DoesNotContain("additional_tools", ((Dictionary<string, object?>)history[0]!).Keys);
        Assert.Equal("c", JsonDictionaryValue.String((Dictionary<string, object?>)history[1]!, "call_id"));
        Assert.Equal(3, JsonDictionaryValue.List(request, "input").Count);
        Assert.Equal("task", JsonDictionaryValue.String((Dictionary<string, object?>)MultiAgentProtocol.InitialHistory(Payload("""{"input":"task"}"""))[0]!, "content"));
    }

    [Theory]
    [InlineData("multi_agent_call")]
    [InlineData("multi_agent_call_output")]
    public void InitialHistory_RejectsExternalHostedRun(string type) => Assert.Throws<BadRequestException>(() => MultiAgentProtocol.InitialHistory(Payload($$"""{"input":[{"type":"{{type}}"}]}""")));

    [Theory]
    [InlineData("\"hello\"")]
    [InlineData("[{\"type\":\"text\",\"text\":\"hello\"}]")]
    [InlineData("[{\"type\":\"encrypted_content\",\"encrypted_content\":\"hello\"}]")]
    public void InitialHistory_ConvertsPlainAgentMessages(string content)
    {
        var history = MultiAgentProtocol.InitialHistory(Payload($$"""{"input":[{"type":"agent_message","author":"/root/a","recipient":"/root","content":{{content}}}]}"""));
        Assert.Equal("Agent message from /root/a to /root:\nhello", JsonDictionaryValue.String((Dictionary<string, object?>)history.Single()!, "content"));
    }

    [Fact]
    public void InitialHistory_RejectsExternalEncryptedAgentMessage() => Assert.Throws<BadRequestException>(() => MultiAgentProtocol.InitialHistory(Payload("""{"input":[{"type":"agent_message","content":[{"type":"encrypted_content","encrypted_content":"enc_opaque"}]}]}""")));

    [Fact]
    public void Updates_OmittedFieldsInheritAndCopiesAreIndependent()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("""{"model":"fixed","instructions":"original","tools":[{"type":"function","name":"read"}],"reasoning":{"effort":"low"}}"""));
        var updated = MultiAgentProtocol.ApplyUpdates(current, Payload("""{"model":"ignored","input":"next","multi_agent":{},"previous_response_id":"old","unknown":true}"""));
        Assert.Equal("fixed", updated["model"]);
        Assert.Equal("original", updated["instructions"]);
        Assert.Equal(7, Tools(updated).Count);
        foreach (var key in new[] { "input", "multi_agent", "previous_response_id", "unknown" }) Assert.False(updated.ContainsKey(key));
        ((Dictionary<string, object?>)updated["reasoning"]!)["effort"] = "high";
        Assert.Equal("low", ((Dictionary<string, object?>)current["reasoning"]!)["effort"]);
    }

    [Fact]
    public void Updates_EmptyToolsRevokeClientToolsButKeepInternalAndHistoricalOutputs()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("""{"tools":[{"type":"custom","name":"execute","format":{"type":"text"}}]}"""));
        var incoming = Payload("""{"tools":[],"input":[{"type":"function_call_output","call_id":"old","output":"done"}]}""");
        var result = MultiAgentProtocol.ApplyUpdates(current, incoming);
        Assert.Equal(6, Tools(result).Count);
        Assert.All(Tools(result), t => Assert.NotNull(MultiAgentProtocol.ActionName(Name(t))));
        Assert.Equal("old", JsonDictionaryValue.String((Dictionary<string, object?>)MultiAgentProtocol.InitialHistory(incoming).Single()!, "call_id"));
    }

    [Fact]
    public void Updates_AdditionalToolsMergeNestedNamespacesAndReplaceMatchingDefinition()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("""{"tools":[{"type":"namespace","name":"files","tools":[{"type":"custom","name":"read","description":"old"},{"type":"function","name":"write"}]}]}"""));
        var incoming = Payload("""{"input":[{"role":"user","additional_tools":[{"type":"namespace","name":"files","tools":[{"type":"custom","name":"read","description":"new"},{"type":"function","name":"list"}]}]}]}""");
        var children = Tools(Tools(MultiAgentProtocol.ApplyUpdates(current, incoming)).Single(t => Name(t) == "files"));
        Assert.Equal(new[] { "read", "write", "list" }, children.Select(Name));
        Assert.Equal("new", children[0]["description"]);
        Assert.Equal("old", Tools(Tools(current).Single(t => Name(t) == "files"))[0]["description"]);
    }

    [Fact]
    public void Updates_ExplicitFieldsReplaceAndNullClears()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("""{"instructions":"old","reasoning":{"effort":"low"},"text":{"format":{"type":"text"}},"tool_choice":"auto","temperature":0.2}"""));
        var updated = MultiAgentProtocol.ApplyUpdates(current, Payload("""{"instructions":"new","reasoning":{"effort":"high"},"text":null,"tool_choice":"required","temperature":0.7}"""));
        Assert.Equal("new", updated["instructions"]);
        Assert.Equal("high", ((Dictionary<string, object?>)updated["reasoning"]!)["effort"]);
        Assert.False(updated.ContainsKey("text"));
        Assert.Equal("required", updated["tool_choice"]);
        Assert.Equal(0.7, updated["temperature"]);
        Assert.False(MultiAgentProtocol.ApplyUpdates(updated, Payload("""{"instructions":null}""")).ContainsKey("instructions"));
    }

    [Fact]
    public void Updates_ExplicitReplacementPrecedesAdditionalTools()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("""{"tools":[{"type":"function","name":"removed"}]}"""));
        var result = MultiAgentProtocol.ApplyUpdates(current, Payload("""{"tools":[{"type":"function","name":"read","description":"old"}],"input":[{"type":"additional_tools","tools":[{"type":"custom","name":"read","format":{"type":"text"}}]}]}"""));
        Assert.Equal(7, Tools(result).Count);
        Assert.DoesNotContain(Tools(result), tool => Name(tool) == "removed");
        Assert.Equal("custom", Tools(result).Single(tool => Name(tool) == "read")["type"]);
    }

    [Fact]
    public void Updates_RejectsClientImpersonationOfServerAction()
    {
        var current = MultiAgentProtocol.NormalizeRequest(Payload("{}"));
        Assert.Throws<BadRequestException>(() => MultiAgentProtocol.ApplyUpdates(current,
            Payload("""{"tools":[{"type":"function","name":"ocxp_ma_list_agents"}]}""")));
    }

    [Fact]
    public void Normalize_DeepNamespacesMergeWithinTheirOwnScope()
    {
        var result = MultiAgentProtocol.NormalizeRequest(Payload("""{"tools":[{"type":"namespace","name":"a","tools":[{"type":"namespace","name":"inner","tools":[{"type":"function","name":"read"}]}]},{"type":"namespace","name":"b","tools":[{"type":"function","name":"read"}]}],"input":[{"type":"additional_tools","tools":[{"type":"namespace","name":"a","tools":[{"type":"namespace","name":"inner","tools":[{"type":"function","name":"write"}]}]}]}]}"""));
        Assert.Equal(new[] { "read", "write" }, Tools(Tools(Tools(result).Single(t => Name(t) == "a")).Single()).Select(Name));
        Assert.Single(Tools(Tools(result).Single(t => Name(t) == "b")));
    }

    private static Dictionary<string, object?> Payload(string json) => (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(JsonDocument.Parse(json).RootElement)!;
    private static List<Dictionary<string, object?>> Tools(IReadOnlyDictionary<string, object?> request) => JsonDictionaryValue.List(request, "tools").Cast<Dictionary<string, object?>>().ToList();
    private static string Name(IReadOnlyDictionary<string, object?> tool) => JsonDictionaryValue.String(tool, "name");
}
