using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentToolCallValidationTests
{
    [Theory]
    [InlineData("undeclared")]
    [InlineData("string_arguments")]
    [InlineData("wrong_execution")]
    [InlineData("wrong_namespace")]
    public void ClientToolSearchRequiresItsDeclaredExecutionAndObjectArguments(string failure)
    {
        var request = Payload("""{"tools":[{"type":"tool_search","execution":"client"}]}""");
        var call = Payload("""{"type":"tool_search_call","name":"tool_search","call_id":"search","execution":"client","arguments":{"query":"browser"}}""");
        ProtocolConverter.ValidateResponsesToolCalls(request, Response(call));
        if (failure == "undeclared") request["tools"] = new List<object?>();
        if (failure == "string_arguments") call["arguments"] = "{}";
        if (failure == "wrong_execution") call["execution"] = "unexpected";
        if (failure == "wrong_namespace") call["namespace"] = "invented";
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(request, Response(call)));
    }

    [Theory]
    [InlineData("exec", null, "function_call", "{}")]
    [InlineData("exec", "functions", "function_call", "{}")]
    [InlineData("exec", "other", "custom_tool_call", "text(1);")]
    [InlineData("exec", "missing", "custom_tool_call", "text(1);")]
    [InlineData("functions__exec", null, "custom_tool_call", "text(1);")]
    [InlineData("exec_command_placeholder_note", null, "function_call", "{}")]
    [InlineData("exec", "other", "function_call", "[]")]
    [InlineData("exec", "other", "function_call", "not-json")]
    [InlineData("exec", "other", "function_call", "{\"x\":1,\"x\":2}")]
    public void RejectsUndeclaredIdentityTypeOrInvalidFunctionEnvelope(string name, string? ns, string type, string payload)
    {
        var response = Response(Call(name, ns, type, payload));
        var before = WebSearchPayload.JsonDumps(response);
        var error = Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Request(), response));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(before, WebSearchPayload.JsonDumps(response));
        Assert.DoesNotContain(payload, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("function_call", "other", "arguments")]
    [InlineData("custom_tool_call", "functions", "input")]
    public void PayloadMustBeAStringAndUseTheDeclaredField(string type, string ns, string field)
    {
        var call = Call("exec", ns, type, "{}");
        call[field] = new Dictionary<string, object?>();
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Request(), Response(call)));
        call.Remove(field);
        call[field == "input" ? "arguments" : "input"] = "secret-payload";
        var error = Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Request(), Response(call)));
        Assert.DoesNotContain("secret-payload", error.Message);
    }

    [Fact]
    public void ValidatesWholeBatchWithoutChangingIdentityOrLiteralCustomInput()
    {
        const string input = "{\"input\":\"literal\"}\ntext(1);";
        var custom = Call("exec", "functions", "custom_tool_call", input);
        var function = Call("exec", "other", "function_call", "{}");
        function["call_id"] = "call-function";
        var response = Response(custom, function);
        var before = WebSearchPayload.JsonDumps(response);
        ProtocolConverter.ValidateResponsesToolCalls(Request(), response);
        Assert.Equal(before, WebSearchPayload.JsonDumps(response));
        function["namespace"] = "wrong";
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Request(), response));
    }

    [Theory]
    [InlineData("additional_tools")]
    [InlineData("tool_search_output")]
    public void DynamicInputToolsArePartOfTheEffectiveRequestContract(string inputType)
    {
        var request = Payload("""{"tools":[],"input":[]}""");
        request["input"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = inputType, ["tools"] = Request()["tools"] }
        };
        ProtocolConverter.ValidateResponsesToolCalls(request, Response(Call("exec", "functions", "custom_tool_call", "text(1);")));
    }

    [Fact]
    public void EmptyToolSetAndDuplicateCallIdsAreRejected()
    {
        var call = Call("exec", "functions", "custom_tool_call", "text(1);");
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Payload("""{"tools":[]}"""), Response(call)));
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(Request(), Response(call, call)));
    }

    [Fact]
    public void FlatDeclaredSeparatorsAndNestedNamespaceLeavesAreExactIdentities()
    {
        var request = Payload("""{"tools":[{"type":"function","name":"flat__name"},{"type":"namespace","name":"outer","tools":[{"type":"namespace","name":"inner","tools":[{"type":"custom","name":"read__file"}]}]}]}""");
        ProtocolConverter.ValidateResponsesToolCalls(request, Response(Call("flat__name", null, "function_call", "{}")));
        ProtocolConverter.ValidateResponsesToolCalls(request, Response(Call("read__file", "outer__inner", "custom_tool_call", "raw")));
        Assert.Throws<UpstreamException>(() => ProtocolConverter.ValidateResponsesToolCalls(request,
            Response(Call("name", "flat", "function_call", "{}"))));
    }

    private static Dictionary<string, object?> Request() => Payload("""
        {"tools":[{"type":"namespace","name":"functions","tools":[{"type":"custom","name":"exec"}]},
        {"type":"namespace","name":"other","tools":[{"type":"function","name":"exec"}]}]}
        """);

    private static Dictionary<string, object?> Call(string name, string? ns, string type, string payload)
    {
        var call = new Dictionary<string, object?>
        {
            ["type"] = type, ["name"] = name, ["call_id"] = "call", ["status"] = "completed",
            [type == "custom_tool_call" ? "input" : "arguments"] = payload
        };
        if (ns is not null) call["namespace"] = ns;
        return call;
    }

    private static Dictionary<string, object?> Response(params Dictionary<string, object?>[] calls) =>
        new() { ["status"] = "completed", ["output"] = calls.Cast<object?>().ToList() };

    private static Dictionary<string, object?> Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)!;
    }
}
