using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Errors;
using OpenCodex.ProtocolConversion.Models;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class CanonicalFacadeRegressionTests
{
    private static readonly ProtocolConversionFacade Facade = new();
    private static D Payload(string json) => (D)ProtocolJson.FromJsonElement(JsonSerializer.Deserialize<JsonElement>(json))!;
    private static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value);
    private static D Object(object? value) => Assert.IsType<D>(value);
    private static List<object?> List(object? value) => Assert.IsType<List<object?>>(value);
    private static CanonicalRequest RequestSnapshot(CanonicalRequest value) => JsonSerializer.Deserialize<CanonicalRequest>(JsonSerializer.Serialize(value))!;
    private static CanonicalResponse ResponseSnapshot(CanonicalResponse value) => JsonSerializer.Deserialize<CanonicalResponse>(JsonSerializer.Serialize(value))!;

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void DirectCanonicalInvocationHasCorrectTargetIdentityAndPayload(ProtocolKind target)
    {
        var request = new CanonicalRequest
        {
            Model = "client",
            Messages = [new() { Role = "assistant", ToolCalls = [new()
            {
                Id = "call-direct", Name = "lookup", Arguments = Element(new D { ["query"] = "中文😀", ["count"] = 7 }),
                ExtensionData = new() { ["id"] = Element("stale-id"), ["function"] = Element(new D { ["name"] = "stale-name" }), ["arguments"] = Element("stale-args") }
            }] }, new() { Role = "tool", ToolCallId = "call-direct", Content = [new() { Type = "text", Text = "actual-result" }] }],
            ExtensionData = new() { ["model"] = Element("stale-model"), ["messages"] = Element("stale-history") }
        };
        var wire = Facade.ConvertRequest(request, target, "upstream");
        Assert.Equal("upstream", wire["model"]);
        AssertFunctionInvocation(wire, target, "call-direct", "lookup", "中文😀", 7);
        Assert.Contains("actual-result", JsonSerializer.Serialize(wire));
        Assert.DoesNotContain("stale-", JsonSerializer.Serialize(wire));
    }

    [Theory]
    [InlineData(ProtocolKind.Chat, ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Chat, ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Chat, ProtocolKind.Responses)]
    [InlineData(ProtocolKind.Messages, ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages, ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Messages, ProtocolKind.Responses)]
    [InlineData(ProtocolKind.Responses, ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Responses, ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses, ProtocolKind.Responses)]
    public void ReadThenModifyUsesTypedInvocationInsteadOfStaleExtension(ProtocolKind source, ProtocolKind target)
    {
        var payload = source switch
        {
            ProtocolKind.Chat => Payload("""{"model":"m","messages":[{"role":"assistant","tool_calls":[{"id":"call-read","type":"function","function":{"name":"original","arguments":"{\"query\":\"old\",\"count\":1}"}}]},{"role":"tool","tool_call_id":"call-read","content":"actual-result"}]}"""),
            ProtocolKind.Messages => Payload("""{"model":"m","max_tokens":100,"messages":[{"role":"assistant","content":[{"type":"tool_use","id":"call-read","name":"original","input":{"query":"old","count":1}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call-read","content":"actual-result"}]}]}"""),
            ProtocolKind.Responses => Payload("""{"model":"m","input":[{"type":"function_call","call_id":"call-read","name":"original","arguments":"{\"query\":\"old\",\"count\":1}"},{"type":"function_call_output","call_id":"call-read","output":"actual-result"}]}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var originalWire = JsonSerializer.Serialize(payload);
        var request = RequestSnapshot(Facade.ToCanonicalRequest(payload, source));
        var call = request.Messages.SelectMany(message => message.ToolCalls ?? []).Single();
        Assert.Equal("original", call.Name);
        var owner = request.Messages.Single(message => message.ToolCalls?.Count > 0);
        owner.ToolCalls![0] = new() { Id = call.Id, Name = "renamed", Arguments = Element(new D { ["query"] = "fresh😀", ["count"] = 9 }),
            NativeType = call.NativeType, ExtensionData = new() { ["function"] = Element(new D { ["name"] = "original", ["arguments"] = "stale" }) } };
        var converted = Facade.ConvertRequest(request, target, "upstream");
        AssertFunctionInvocation(converted, target, "call-read", "renamed", "fresh😀", 9);
        Assert.Equal(originalWire, JsonSerializer.Serialize(payload));
    }

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void NamespacedCustomInvocationPreservesRawSourceAndActualWirePlan(ProtocolKind target)
    {
        const string raw = " \ntext(\"中文 😀\"); {\"input\":\"literal\"}\n ";
        var payload = new D
        {
            ["model"] = "m",
            ["tools"] = new List<object?> {
                new D { ["type"] = "namespace", ["name"] = "alpha", ["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec", ["format"] = new D { ["type"] = "text" } } } },
                new D { ["type"] = "namespace", ["name"] = "beta", ["tools"] = new List<object?> { new D { ["type"] = "custom", ["name"] = "exec", ["format"] = new D { ["type"] = "text" } } } }
            },
            ["tool_choice"] = new D { ["type"] = "custom", ["name"] = "exec", ["namespace"] = "alpha" },
            ["input"] = new List<object?> { new D { ["type"] = "custom_tool_call", ["call_id"] = "custom-id", ["name"] = "exec", ["namespace"] = "alpha", ["input"] = raw },
                new D { ["type"] = "custom_tool_call_output", ["call_id"] = "custom-id", ["output"] = "result😀" } }
        };
        var canonical = RequestSnapshot(Facade.ToCanonicalRequest(payload, ProtocolKind.Responses));
        var call = canonical.Messages.SelectMany(message => message.ToolCalls ?? []).Single();
        Assert.Equal("exec", call.Name); Assert.Equal("alpha", call.Namespace);
        Assert.Equal("custom", call.NativeType); Assert.Equal(raw, call.Arguments.GetString());
        var converted = Facade.ConvertRequestWithMetadata(canonical, target, "upstream");
        if (target == ProtocolKind.Responses)
        {
            var invocation = Object(List(converted.Request["input"]).Single(item => Object(item).GetValueOrDefault("type") is "custom_tool_call"));
            Assert.Equal("custom-id", invocation["call_id"]); Assert.Equal("exec", invocation["name"]);
            Assert.Equal("alpha", invocation["namespace"]); Assert.Equal(raw, invocation["input"]);
            Assert.DoesNotContain("arguments", invocation.Keys);
            Assert.Contains(List(converted.Request["input"]), item => Object(item).GetValueOrDefault("type") is "custom_tool_call_output");
        }
        else
        {
            var map = converted.ToolCallMappings!["alpha__exec"];
            Assert.Equal("custom", map.NativeType); Assert.Equal("exec", map.ResponsesName); Assert.Equal("alpha", map.Namespace);
            if (target == ProtocolKind.Chat)
            {
                var invocation = Object(List(Object(List(converted.Request["messages"])[0])["tool_calls"])[0]);
                var function = Object(invocation["function"]);
                Assert.Equal("alpha__exec", function["name"]);
                Assert.Equal(raw, Payload((string)function["arguments"]!)["input"]);
            }
            else
            {
                var invocation = Object(List(Object(List(converted.Request["messages"])[0])["content"]).Single(item => Object(item).GetValueOrDefault("type") is "tool_use"));
                Assert.Equal("alpha__exec", invocation["name"]); Assert.Equal(raw, Object(invocation["input"])["input"]);
            }
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Responses, ProtocolKind.Chat, "previous_response_id")]
    [InlineData(ProtocolKind.Responses, ProtocolKind.Messages, "conversation")]
    [InlineData(ProtocolKind.Messages, ProtocolKind.Chat, "container")]
    [InlineData(ProtocolKind.Messages, ProtocolKind.Responses, "thinking")]
    [InlineData(ProtocolKind.Chat, ProtocolKind.Messages, "parallel_tool_calls")]
    [InlineData(ProtocolKind.Chat, ProtocolKind.Messages, "reasoning_effort")]
    public void SourceSemanticRulesAreSharedWithStaticConverter(ProtocolKind source, ProtocolKind target, string parameter)
    {
        var wire = source == ProtocolKind.Responses ? Payload("""{"model":"m","input":"hello"}""") : Payload("""{"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hello"}]}""");
        wire[parameter] = parameter == "parallel_tool_calls" ? true : "explicit";
        var staticFailure = Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(wire, source.ToWireValue(), target.ToWireValue(), "m"));
        var canonical = Facade.ToCanonicalRequest(wire, source);
        var facadeFailure = Assert.Throws<BadRequestException>(() => Facade.ConvertRequest(canonical, target, "m"));
        Assert.Equal(staticFailure.Message, facadeFailure.Message);
    }

    [Fact]
    public void ExtensionSemanticViewIsValidatedAndNeutralConstructionDoesNotGuessSource()
    {
        var explicitSource = new CanonicalRequest { SourceProtocol = ProtocolKind.Responses, Model = "m", ExtensionData = new() { ["previous_response_id"] = Element("prior") } };
        Assert.Throws<BadRequestException>(() => Facade.ConvertRequest(explicitSource, ProtocolKind.Chat, "m"));
        var neutral = new CanonicalRequest { Model = "m", Parameters = new D { ["previous_response_id"] = "prior" }.ToDictionary(pair => pair.Key, pair => Element(pair.Value)) };
        Assert.Contains("SourceProtocol", Assert.Throws<BadRequestException>(() => Facade.ConvertRequest(neutral, ProtocolKind.Chat, "m")).Message);
        var plain = new CanonicalRequest { Model = "m", Messages = [new() { Role = "user", Content = [new() { Type = "text", Text = "hello" }] }] };
        Assert.Equal("upstream", Facade.ConvertRequest(plain, ProtocolKind.Responses, "upstream")["model"]);
    }

    [Fact]
    public void SameChatPreservesKnownOptionsAndUnknownHistoryWithoutBridgeLoss()
    {
        var payload = Payload("""{"model":"m","reasoning_effort":"high","response_format":{"type":"json_object"},"vendor_flag":true,"messages":[{"role":"user","content":"hello","vendor_message":17}]}""");
        var request = RequestSnapshot(Facade.ToCanonicalRequest(payload, ProtocolKind.Chat));
        var wire = Facade.ConvertRequest(request, ProtocolKind.Chat, "upstream");
        Assert.Equal("high", wire["reasoning_effort"]); Assert.Equal("json_object", Object(wire["response_format"])["type"]);
        Assert.Equal(true, wire["vendor_flag"]); Assert.Equal(17, Object(List(wire["messages"])[0])["vendor_message"]);
    }

    [Theory]
    [InlineData(ProtocolKind.Chat, """{"id":"chat","object":"chat.completion","model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"chat-text","vendor_message":{"a":1}}}],"usage":{"prompt_tokens":5,"completion_tokens":7,"total_tokens":12,"prompt_tokens_details":{"cached_tokens":3,"audio_tokens":2},"completion_tokens_details":{"reasoning_tokens":4},"vendor_usage":17},"vendor_root":[1,2]}""")]
    [InlineData(ProtocolKind.Messages, """{"id":"msg","type":"message","role":"assistant","model":"m","content":[{"type":"thinking","thinking":"hidden","signature":"signature","vendor_block":19},{"type":"text","text":"messages-text","vendor_text":23}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":11,"output_tokens":13,"cache_read_input_tokens":5,"cache_creation_input_tokens":7,"vendor_usage":{"billable":2}},"vendor_root":"kept"}""")]
    [InlineData(ProtocolKind.Responses, """{"id":"resp","object":"response","model":"m","created_at":123,"status":"completed","output":[{"id":"message-id","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"responses-text","annotations":[],"vendor_part":29}],"vendor_message":31}],"usage":{"input_tokens":17,"output_tokens":19,"total_tokens":36,"input_tokens_details":{"cached_tokens":2,"audio_tokens":3},"output_tokens_details":{"reasoning_tokens":5}},"vendor_root":{"kept":true}}""")]
    public void SameProtocolJsonSnapshotPreservesCompleteWireFromIndependentSamples(ProtocolKind source, string sample)
    {
        var payload = Payload(sample);
        var snapshot = ResponseSnapshot(Facade.ToCanonicalResponse(payload, source));
        Assert.Equal(source, snapshot.SourceProtocol);
        var restored = Facade.ConvertResponse(snapshot, source);
        Assert.True(JsonElement.DeepEquals(Element(payload), Element(restored)), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void TypedResponseChangesWinOverRawAndReservedExtensionsWithoutLosingUsageDetails()
    {
        var payload = Payload("""{"id":"chat","model":"old","created":123,"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"old-text","vendor_message":17}}],"usage":{"prompt_tokens":5,"completion_tokens":7,"total_tokens":12,"prompt_tokens_details":{"cached_tokens":3,"audio_tokens":2},"completion_tokens_details":{"reasoning_tokens":4}},"vendor_root":19}""");
        var node = JsonNode.Parse(JsonSerializer.Serialize(Facade.ToCanonicalResponse(payload, ProtocolKind.Chat)))!.AsObject();
        node["Model"] = "new-model"; node["Text"] = "new-text";
        node["Usage"]!["InputTokens"] = 17; node["Usage"]!["CachedTokens"] = 5;
        node["choices"] = JsonNode.Parse("""[{"message":{"content":"stale-extension"}}]""");
        node["usage"] = JsonNode.Parse("""{"prompt_tokens":999}""");
        var modified = JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!;
        var restored = Facade.ConvertResponse(modified, ProtocolKind.Chat);
        Assert.Equal("new-model", restored["model"]);
        var message = Object(Object(List(restored["choices"])[0])["message"]);
        Assert.Equal("new-text", message["content"]); Assert.Equal(17, message["vendor_message"]);
        var usage = Object(restored["usage"]);
        Assert.Equal(17, usage["prompt_tokens"]); Assert.Equal(5, Object(usage["prompt_tokens_details"])["cached_tokens"]);
        Assert.Equal(2, Object(usage["prompt_tokens_details"])["audio_tokens"]);
        Assert.Equal(4, Object(usage["completion_tokens_details"])["reasoning_tokens"]);
        Assert.Equal(19, restored["vendor_root"]);
        Assert.Equal("old", payload["model"]);
    }

    [Fact]
    public void MessagesTextEditPreservesUnchangedThinkingToolsAndBlockExtensions()
    {
        var sample = """{"id":"msg-edit","type":"message","role":"assistant","model":"m","content":[{"type":"thinking","thinking":"hidden","signature":"signed","vendor_block":19},{"type":"text","text":"old-text","vendor_text":23},{"type":"tool_use","id":"call-stable","name":"lookup","input":{"query":"keep"},"vendor_tool":29},{"type":"vendor_content","payload":{"stable":31}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":11,"output_tokens":13},"vendor_root":37}""";
        var payload = Payload(sample);
        var snapshot = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(payload, ProtocolKind.Messages))))!.AsObject();
        snapshot["Text"] = "new-text";
        var actual = Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(snapshot.ToJsonString())!, ProtocolKind.Messages);
        var expected = JsonNode.Parse(sample)!.AsObject();
        expected["content"]![1]!["text"] = "new-text";
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected.ToJsonString()), Element(actual)), JsonSerializer.Serialize(actual));
        var blocks = List(actual["content"]);
        Assert.Equal("hidden", Object(blocks[0])["thinking"]);
        Assert.Equal("signed", Object(blocks[0])["signature"]);
        Assert.Equal(19, Object(blocks[0])["vendor_block"]);
        Assert.Equal(23, Object(blocks[1])["vendor_text"]);
        Assert.Equal("old-text", Object(List(payload["content"])[1])["text"]);
    }

    [Fact]
    public void ResponsesTextEditPreservesUnchangedOutputIdentityReasoningToolsAndExtensions()
    {
        var sample = """{"id":"resp-edit","object":"response","model":"m","created_at":123,"status":"completed","output":[{"id":"reasoning-stable","type":"reasoning","status":"completed","summary":[{"type":"summary_text","text":"hidden"}],"encrypted_content":"opaque-signature","vendor_reasoning":19},{"id":"message-stable","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"old-text","annotations":[],"vendor_part":23}],"vendor_message":29},{"id":"tool-stable","type":"function_call","status":"completed","call_id":"call-stable","name":"lookup","arguments":"{\"query\":\"keep\"}","vendor_tool":31},{"id":"opaque-stable","type":"vendor_output","payload":{"stable":37}}],"usage":{"input_tokens":17,"output_tokens":19,"total_tokens":36},"vendor_root":41}""";
        var payload = Payload(sample);
        var snapshot = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(payload, ProtocolKind.Responses))))!.AsObject();
        snapshot["Text"] = "new-text";
        var actual = Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(snapshot.ToJsonString())!, ProtocolKind.Responses);
        var expected = JsonNode.Parse(sample)!.AsObject();
        expected["output"]![1]!["content"]![0]!["text"] = "new-text";
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected.ToJsonString()), Element(actual)), JsonSerializer.Serialize(actual));
        Assert.Equal("reasoning-stable", Object(List(actual["output"])[0])["id"]);
        Assert.Equal("opaque-signature", Object(List(actual["output"])[0])["encrypted_content"]);
        Assert.Equal("message-stable", Object(List(actual["output"])[1])["id"]);
        Assert.Equal("tool-stable", Object(List(actual["output"])[2])["id"]);
    }

    [Theory]
    [InlineData(ProtocolKind.Messages, """{"id":"m","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}],"stop_reason":"end_turn","usage":{}}""")]
    [InlineData(ProtocolKind.Responses, """{"id":"r","object":"response","model":"m","status":"completed","output":[{"id":"one","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"a"},{"type":"output_text","text":"b"}]}],"usage":{}}""")]
    public void AggregateTextEditsRejectAnAmbiguousRawTarget(ProtocolKind source, string sample)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(Payload(sample), source))))!.AsObject();
        node["Text"] = "new-aggregate";
        Assert.Contains("exactly one original text block", Assert.Throws<BadRequestException>(() =>
            Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, source)).Message);
    }

    [Theory]
    [InlineData(ProtocolKind.Messages, """{"id":"m","type":"message","role":"assistant","model":"m","content":[{"type":"thinking","thinking":"hidden","signature":"signed"},{"type":"text","text":"old"}],"stop_reason":"end_turn","usage":{}}""")]
    [InlineData(ProtocolKind.Responses, """{"id":"r","object":"response","model":"m","status":"completed","output":[{"id":"reason","type":"reasoning","summary":[{"type":"summary_text","text":"hidden"}],"encrypted_content":"signed"},{"id":"message","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"old"}]}],"usage":{}}""")]
    public void StructuredRawResponseEditsFailInsteadOfDiscardingOriginalBlocks(ProtocolKind source, string sample)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(Payload(sample), source))))!.AsObject();
        node["Reasoning"] = "modified-hidden";
        Assert.Contains("Structured changes", Assert.Throws<BadRequestException>(() =>
            Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, source)).Message);
    }

    [Theory]
    [InlineData("incomplete", "stop")]
    [InlineData("completed", "length")]
    [InlineData("in_progress", "stop")]
    public void ContradictoryResponsesStatusEditsFailExplicitly(string status, string finish)
    {
        var original = Payload("""{"id":"r","object":"response","model":"m","created_at":123,"status":"completed","output":[{"id":"stable","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello"}]}],"usage":{}}""");
        var node = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(original, ProtocolKind.Responses))))!.AsObject();
        node["Status"] = status; node["FinishReason"] = finish;
        Assert.Throws<BadRequestException>(() => Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, ProtocolKind.Responses));
        Assert.Equal("completed", original["status"]);
    }

    [Fact]
    public void ConsistentIncompleteStatusEditPreservesUntouchedRawOutput()
    {
        var original = Payload("""{"id":"r","object":"response","model":"m","created_at":123,"status":"completed","output":[{"id":"stable","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","vendor_part":19}]}],"usage":{},"vendor_root":23}""");
        var node = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(original, ProtocolKind.Responses))))!.AsObject();
        node["Status"] = "incomplete"; node["FinishReason"] = "length";
        var actual = Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, ProtocolKind.Responses);
        Assert.Equal("incomplete", actual["status"]);
        Assert.Equal("max_output_tokens", Object(actual["incomplete_details"])["reason"]);
        Assert.True(JsonElement.DeepEquals(Element(original["output"]), Element(actual["output"])));
        Assert.Equal(23, actual["vendor_root"]);
    }

    [Theory]
    [InlineData("incomplete", "stop")]
    [InlineData("completed", "length")]
    [InlineData("in_progress", "stop")]
    public void DirectCanonicalResponseCannotSilentlyIgnoreExplicitStatus(string status, string finish)
    {
        var response = new CanonicalResponse { Id = "direct", Model = "m", Status = status, FinishReason = finish, Text = "hello" };
        Assert.Throws<BadRequestException>(() => Facade.ConvertResponse(response, ProtocolKind.Responses));
    }

    [Theory]
    [InlineData(null, "stop", "completed")]
    [InlineData(null, "length", "incomplete")]
    [InlineData("incomplete", "length", "incomplete")]
    public void NeutralOrConsistentDirectResponseRendersAccordingToFinishReason(string? status, string finish, string expected)
    {
        var response = new CanonicalResponse { Id = "direct", Model = "m", Status = status, FinishReason = finish, Text = "hello" };
        var wire = Facade.ConvertResponse(response, ProtocolKind.Responses);
        Assert.Equal(expected, wire["status"]);
    }

    [Fact]
    public void UnmodifiedRawInProgressResponsePreservesItsWireState()
    {
        var original = Payload("""{"id":"r","object":"response","model":"m","status":"in_progress","output":[],"vendor_root":19}""");
        var snapshot = ResponseSnapshot(Facade.ToCanonicalResponse(original, ProtocolKind.Responses));
        var actual = Facade.ConvertResponse(snapshot, ProtocolKind.Responses);
        Assert.True(JsonElement.DeepEquals(Element(original), Element(actual)));
    }

    [Theory]
    [InlineData(ProtocolKind.Messages, """{"id":"m","type":"error","error":{"type":"upstream_error","message":"failed"}}""")]
    [InlineData(ProtocolKind.Responses, """{"id":"r","object":"response","status":"failed","error":{"type":"upstream_error","message":"failed"}}""")]
    [InlineData(ProtocolKind.Responses, """{"id":"r","object":"response","status":"completed","output":[{"id":"message","type":"message","role":"assistant"}]}""")]
    public void TextEditWithoutOriginalContentNodesFailsWithBadRequest(ProtocolKind source, string sample)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(ResponseSnapshot(Facade.ToCanonicalResponse(Payload(sample), source))))!.AsObject();
        node["Text"] = "new-text";
        Assert.Throws<BadRequestException>(() => Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, source));
    }

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void DirectCustomDefinitionPreservesTypeFormatAndRawInputWithoutRawExtension(ProtocolKind target)
    {
        const string input = " \ntext('中文😀');\n ";
        var request = new CanonicalRequest
        {
            Model = "m",
            Tools = [new() { Name = "exec", Type = "custom", ExtensionData = new() { ["format"] = Element(new D { ["type"] = "text" }) } }],
            Messages = [new() { Role = "assistant", ToolCalls = [new() { Id = "direct-custom", Name = "exec", NativeType = "custom", Arguments = Element(input) }] },
                new() { Role = "tool", ToolCallId = "direct-custom", Content = [new() { Type = "text", Text = "actual-result" }] }]
        };
        var converted = Facade.ConvertRequestWithMetadata(request, target, "upstream");
        var tool = Object(List(converted.Request["tools"])[0]);
        if (target == ProtocolKind.Responses)
        {
            Assert.Equal("custom", tool["type"]); Assert.Equal("text", Object(tool["format"])["type"]);
            var call = Object(List(converted.Request["input"]).Single(item => Object(item).GetValueOrDefault("type") is "custom_tool_call"));
            Assert.Equal(input, call["input"]); Assert.Equal("exec", call["name"]);
        }
        else
        {
            Assert.Equal("custom", converted.ToolCallMappings!["exec"].NativeType);
            if (target == ProtocolKind.Chat)
            {
                var call = Object(List(Object(List(converted.Request["messages"])[0])["tool_calls"])[0]);
                Assert.Equal(input, Payload((string)Object(call["function"])["arguments"]!)["input"]);
            }
            else
            {
                var call = Object(List(Object(List(converted.Request["messages"])[0])["content"]).Single(item => Object(item).GetValueOrDefault("type") is "tool_use"));
                Assert.Equal(input, Object(call["input"])["input"]);
            }
        }
    }

    [Fact]
    public void MessagesCacheAggregateChangesRequireExplicitConsistentDetailCounts()
    {
        var payload = Payload("""{"id":"msg","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":11,"output_tokens":13,"cache_read_input_tokens":5,"cache_creation_input_tokens":7,"vendor_usage":19}}""");
        var node = JsonNode.Parse(JsonSerializer.Serialize(Facade.ToCanonicalResponse(payload, ProtocolKind.Messages)))!.AsObject();
        node["Usage"]!["CachedTokens"] = 20;
        Assert.Throws<BadRequestException>(() => Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, ProtocolKind.Messages));
        node["Usage"]!["cache_read_input_tokens"] = 13;
        node["Usage"]!["cache_creation_input_tokens"] = 7;
        var wire = Facade.ConvertResponse(JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!, ProtocolKind.Messages);
        var usage = Object(wire["usage"]);
        Assert.Equal(13, usage["cache_read_input_tokens"]); Assert.Equal(7, usage["cache_creation_input_tokens"]);
        Assert.Equal(19, usage["vendor_usage"]);
    }

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void TypedToolDefinitionBeatsItsOriginalRawDeclaration(ProtocolKind target)
    {
        var request = Facade.ToCanonicalRequest(Payload("""{"model":"m","tools":[{"type":"function","name":"old","parameters":{"type":"object","properties":{"old":{"type":"string"}}}}],"input":"hello"}"""), ProtocolKind.Responses);
        var old = Assert.Single(request.Tools);
        request.Tools[0] = new() { Name = "new", InputSchema = Element(new D { ["type"] = "object", ["properties"] = new D { ["fresh"] = new D { ["type"] = "integer" } } }), ExtensionData = old.ExtensionData };
        var wire = Facade.ConvertRequest(request, target, "upstream");
        var tool = Object(List(wire["tools"])[0]);
        var definition = target == ProtocolKind.Chat ? Object(tool["function"]) : tool;
        Assert.Equal("new", definition["name"]);
        var schema = Object(definition[target == ProtocolKind.Messages ? "input_schema" : "parameters"]);
        Assert.Contains("fresh", Object(schema["properties"]).Keys); Assert.DoesNotContain("old", Object(schema["properties"]).Keys);
    }

    [Fact]
    public void ConflictingResponseSourcesFailExplicitly()
    {
        var response = ResponseSnapshot(Facade.ToCanonicalResponse(Payload("""{"id":"c","model":"m","choices":[],"usage":{}}"""), ProtocolKind.Chat));
        Assert.Throws<BadRequestException>(() => Facade.ConvertResponse(response, ProtocolKind.Messages, ProtocolKind.Responses));
    }

    private static void AssertFunctionInvocation(D wire, ProtocolKind target, string id, string name, string query, int count)
    {
        D arguments;
        if (target == ProtocolKind.Chat)
        {
            var call = Object(List(Object(List(wire["messages"])[0])["tool_calls"])[0]);
            Assert.Equal(id, call["id"]); Assert.Equal("function", call["type"]);
            var function = Object(call["function"]); Assert.Equal(name, function["name"]);
            arguments = Payload((string)function["arguments"]!);
        }
        else if (target == ProtocolKind.Messages)
        {
            var call = Object(List(Object(List(wire["messages"])[0])["content"]).Single(item => Object(item).GetValueOrDefault("type") is "tool_use"));
            Assert.Equal(id, call["id"]); Assert.Equal(name, call["name"]); arguments = Object(call["input"]);
        }
        else
        {
            var call = Object(List(wire["input"]).Single(item => Object(item).GetValueOrDefault("type") is "function_call"));
            Assert.Equal(id, call["call_id"]); Assert.Equal(name, call["name"]); arguments = Payload((string)call["arguments"]!);
        }
        Assert.Equal(query, arguments["query"]); Assert.Equal(count, arguments["count"]);
    }
}
