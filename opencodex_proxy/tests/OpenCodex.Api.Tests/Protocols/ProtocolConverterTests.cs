using System.Text.Json;
using OpenCodex.Api.Protocols;

namespace OpenCodex.Api.Tests.Protocols;

public sealed class ProtocolConverterTests
{
    [Theory]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses, true)]
    [InlineData(ProtocolConverter.Chat, ProtocolConverter.Chat, true)]
    [InlineData(ProtocolConverter.Messages, ProtocolConverter.Messages, true)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Chat, true)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Messages, true)]
    [InlineData(ProtocolConverter.Chat, ProtocolConverter.Responses, false)]
    [InlineData(ProtocolConverter.Messages, ProtocolConverter.Responses, false)]
    [InlineData(ProtocolConverter.Chat, ProtocolConverter.Messages, false)]
    public void SupportsStreamingConversionMatchesMigratedSsePaths(
        string sourceProtocol,
        string targetProtocol,
        bool expected)
    {
        Assert.Equal(
            expected,
            ProtocolConverter.SupportsStreamingConversion(sourceProtocol, targetProtocol));
    }

    [Fact]
    public void ResponsesStringInputConvertsToChatUserMessage()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", "ping"),
            ("temperature", 0.3));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        Assert.Equal("upstream", result["model"]);
        Assert.Equal(0.3, result["temperature"]);
        var messages = AssertList(result["messages"]);
        var message = AssertObject(messages[0]);
        Assert.Equal("user", message["role"]);
        Assert.Equal("ping", message["content"]);
    }

    [Fact]
    public void ResponsesRequestNormalizesJsonElementValues()
    {
        using var inputDocument = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "input_text", "text": "hi from json" }
                ]
              }
            ]
            """);
        using var parametersDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "q": { "type": "string" }
              }
            }
            """);
        using var maxTokensDocument = JsonDocument.Parse("64");

        var payload = Obj(
            ("model", "local"),
            ("input", inputDocument.RootElement),
            ("tools", List(Obj(
                ("type", "function"),
                ("name", "lookup"),
                ("parameters", parametersDocument.RootElement)))),
            ("max_output_tokens", maxTokensDocument.RootElement));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        Assert.Equal("upstream", result["model"]);
        Assert.Equal(64, result["max_tokens"]);
        Assert.False(result.ContainsKey("max_output_tokens"));

        var message = AssertObject(AssertList(result["messages"])[0]);
        Assert.Equal("user", message["role"]);
        Assert.Equal("hi from json", message["content"]);

        var function = AssertObject(AssertObject(AssertList(result["tools"])[0])["function"]);
        var parameters = AssertObject(function["parameters"]);
        Assert.Equal("object", parameters["type"]);
        Assert.True(AssertObject(parameters["properties"]).ContainsKey("q"));
    }

    [Fact]
    public void ResponsesRequestConvertsToChatWithInstructionsToolsAndMaxTokens()
    {
        var payload = Obj(
            ("model", "local"),
            ("instructions", "be brief"),
            ("input", List(Obj(
                ("role", "user"),
                ("content", List(Obj(("type", "input_text"), ("text", "hi"))))))),
            ("tools", List(Obj(
                ("type", "function"),
                ("name", "lookup"),
                ("description", "Lookup data"),
                ("parameters", Obj(
                    ("type", "object"),
                    ("properties", Obj(("q", Obj(("type", "string")))))))))),
            ("max_output_tokens", 32));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        Assert.Equal("upstream", result["model"]);
        var messages = AssertList(result["messages"]);
        Assert.Equal("system", AssertObject(messages[0])["role"]);
        Assert.Equal("be brief", AssertObject(messages[0])["content"]);
        Assert.Equal("hi", AssertObject(messages[1])["content"]);
        var tool = AssertObject(AssertList(result["tools"])[0]);
        var function = AssertObject(tool["function"]);
        Assert.Equal("lookup", function["name"]);
        Assert.Equal(32, result["max_tokens"]);
        Assert.False(result.ContainsKey("max_output_tokens"));
    }

    [Fact]
    public void ResponsesNativeToolsConvertToChatWrappers()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", "run"),
            ("tools", List(
                Obj(("type", "web_search")),
                Obj(("type", "local_shell"), ("description", "Run shell")),
                Obj(("type", "apply_patch"), ("description", "Apply a patch")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var tools = AssertList(result["tools"]);
        Assert.Equal(
            [
                "web_search",
                "local_shell",
                "apply_patch_add_file",
                "apply_patch_delete_file",
                "apply_patch_update_file",
                "apply_patch_replace_file",
                "apply_patch_batch"
            ],
            tools.Select(tool => AssertObject(AssertObject(tool)["function"])["name"]).ToList());

        var webSearchParameters = AssertObject(AssertObject(AssertObject(tools[0])["function"])["parameters"]);
        Assert.Equal(false, webSearchParameters["additionalProperties"]);
        Assert.Equal(List("query"), AssertList(webSearchParameters["required"]));

        var shellParameters = AssertObject(AssertObject(AssertObject(tools[1])["function"])["parameters"]);
        Assert.Equal(List("cmd"), AssertList(shellParameters["required"]));

        var updateParameters = AssertObject(AssertObject(AssertObject(tools[4])["function"])["parameters"]);
        Assert.Equal(List("path", "hunks"), AssertList(updateParameters["required"]));
        Assert.True(AssertObject(updateParameters["properties"]).ContainsKey("hunks"));
    }

    [Fact]
    public void ResponsesToolHistoryGroupsToolCallsAndOutputs()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", List(
                Obj(("role", "user"), ("content", List(Obj(("type", "input_text"), ("text", "run checks"))))),
                Obj(("type", "function_call"), ("call_id", "call_a"), ("name", "exec_command"), ("arguments", "{\"cmd\":\"pwd\"}")),
                Obj(("type", "function_call"), ("call_id", "call_b"), ("name", "exec_command"), ("arguments", "{\"cmd\":\"ls\"}")),
                Obj(("type", "function_call_output"), ("call_id", "call_a"), ("output", "/tmp")),
                Obj(("type", "function_call_output"), ("call_id", "call_b"), ("output", "file.txt")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var messages = AssertList(result["messages"]);
        Assert.Equal(["user", "assistant", "tool", "tool"], messages.Select(message => AssertObject(message)["role"]).ToList());
        var assistant = AssertObject(messages[1]);
        Assert.Equal(
            ["call_a", "call_b"],
            AssertList(assistant["tool_calls"]).Select(toolCall => AssertObject(toolCall)["id"]).ToList());
        Assert.Equal(
            ["call_a", "call_b"],
            messages.Skip(2).Select(message => AssertObject(message)["tool_call_id"]).ToList());
    }

    [Fact]
    public void ResponsesToolHistoryMergesToolCallsAcrossReasoning()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", List(
                Obj(("role", "user"), ("content", "run checks")),
                Obj(("type", "function_call"), ("call_id", "call_a"), ("name", "exec_command"), ("arguments", "{\"cmd\":\"pwd\"}")),
                Obj(
                    ("type", "reasoning"),
                    ("encrypted_content", "between calls")),
                Obj(("type", "function_call"), ("call_id", "call_b"), ("name", "exec_command"), ("arguments", "{\"cmd\":\"ls\"}")),
                Obj(("type", "function_call_output"), ("call_id", "call_a"), ("output", "/tmp")),
                Obj(("type", "function_call_output"), ("call_id", "call_b"), ("output", "file.txt")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var messages = AssertList(result["messages"]);
        Assert.Equal(["user", "assistant", "tool", "tool"], messages.Select(message => AssertObject(message)["role"]).ToList());
        var assistant = AssertObject(messages[1]);
        Assert.Equal("between calls", assistant["reasoning_content"]);
        Assert.Equal(
            ["call_a", "call_b"],
            AssertList(assistant["tool_calls"]).Select(toolCall => AssertObject(toolCall)["id"]).ToList());
        Assert.Equal(
            ["call_a", "call_b"],
            messages.Skip(2).Select(message => AssertObject(message)["tool_call_id"]).ToList());
    }

    [Fact]
    public void ResponsesReasoningFoldsIntoToolCallAndMissingOutputsAreFilled()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", List(
                Obj(("role", "user"), ("content", "search")),
                Obj(
                    ("type", "reasoning"),
                    ("summary", List(Obj(("type", "summary_text"), ("text", "short")))),
                    ("encrypted_content", "private reasoning")),
                Obj(("type", "function_call"), ("call_id", "call_1"), ("name", "lookup"), ("arguments", "{\"q\":\"cats\"}")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var messages = AssertList(result["messages"]);
        var assistant = AssertObject(messages[1]);
        Assert.Equal("private reasoning", assistant["reasoning_content"]);
        Assert.Equal("call_1", AssertObject(AssertList(assistant["tool_calls"])[0])["id"]);
        var tool = AssertObject(messages[2]);
        Assert.Equal("tool", tool["role"]);
        Assert.Equal("call_1", tool["tool_call_id"]);
        Assert.Contains("tool output missing", Assert.IsType<string>(tool["content"]));
    }

    [Fact]
    public void ResponsesReasoningTextPrefersEncryptedContentThenSummaryThenContent()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", List(
                Obj(("role", "user"), ("content", "reason")),
                Obj(
                    ("type", "reasoning"),
                    ("summary", List(Obj(("type", "summary_text"), ("text", "summary ignored")))),
                    ("content", "content ignored"),
                    ("encrypted_content", "encrypted preferred")),
                Obj(("type", "function_call"), ("call_id", "call_encrypted"), ("name", "lookup"), ("arguments", "{}")),
                Obj(("type", "function_call_output"), ("call_id", "call_encrypted"), ("output", "ok")),
                Obj(
                    ("type", "reasoning"),
                    ("summary", List(Obj(("type", "summary_text"), ("text", "summary fallback")))),
                    ("content", "content ignored")),
                Obj(("type", "function_call"), ("call_id", "call_summary"), ("name", "lookup"), ("arguments", "{}")),
                Obj(("type", "function_call_output"), ("call_id", "call_summary"), ("output", "ok")),
                Obj(
                    ("type", "reasoning"),
                    ("content", "content fallback")),
                Obj(("type", "function_call"), ("call_id", "call_content"), ("name", "lookup"), ("arguments", "{}")),
                Obj(("type", "function_call_output"), ("call_id", "call_content"), ("output", "ok")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var assistantMessages = AssertList(result["messages"])
            .Select(AssertObject)
            .Where(message => Equals(message["role"], "assistant"))
            .ToList();
        Assert.Equal(
            ["encrypted preferred", "summary fallback", "content fallback"],
            assistantMessages.Select(message => message["reasoning_content"]).ToList());
    }

    [Fact]
    public void ResponsesOrphanToolOutputsAreScrubbed()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", List(
                Obj(("role", "user"), ("content", "first")),
                Obj(("type", "function_call_output"), ("call_id", "call_orphan"), ("output", "stale")),
                Obj(("type", "function_call"), ("call_id", "call_a"), ("name", "lookup"), ("arguments", "{}")),
                Obj(("type", "function_call_output"), ("call_id", "call_a"), ("output", "ok")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var toolMessages = AssertList(result["messages"])
            .Select(AssertObject)
            .Where(message => Equals(message["role"], "tool"))
            .ToList();
        Assert.Single(toolMessages);
        Assert.Equal("call_a", toolMessages[0]["tool_call_id"]);
        Assert.Equal("ok", toolMessages[0]["content"]);
    }

    [Fact]
    public void ResponsesNamespaceToolsFlattenForChatAndRoundTripToResponses()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", "run"),
            ("tools", List(
                Obj(
                    ("type", "function"),
                    ("name", "exec_command"),
                    ("description", "shell"),
                    ("parameters", Obj(("type", "object")))),
                Obj(
                    ("type", "namespace"),
                    ("name", "mcp__node_repl"),
                    ("tools", List(
                        Obj(("type", "function"), ("name", "js"), ("description", "Run JS"), ("parameters", Obj(("type", "object")))),
                        Obj(("type", "function"), ("name", "js_reset"), ("description", "Reset"), ("parameters", Obj(("type", "object"))))))))));

        var chat = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var chatToolNames = AssertList(chat["tools"])
            .Select(tool => AssertObject(AssertObject(tool)["function"])["name"])
            .ToList();
        Assert.Equal(["exec_command", "mcp__node_repl__js", "mcp__node_repl__js_reset"], chatToolNames);

        var responses = ProtocolConverter.ConvertRequest(chat, "chat", "responses", "downstream");
        var responseTools = AssertList(responses["tools"]).Select(AssertObject).ToList();
        Assert.Contains(responseTools, tool => Equals(tool["type"], "function") && Equals(tool["name"], "exec_command"));
        var namespaceTool = Assert.Single(responseTools, tool => Equals(tool["type"], "namespace"));
        Assert.Equal("mcp__node_repl", namespaceTool["name"]);
        Assert.Equal(
            ["js", "js_reset"],
            AssertList(namespaceTool["tools"]).Select(tool => AssertObject(tool)["name"]).ToList());
    }

    [Fact]
    public void ResponsesToolsConvertToMessagesInputSchemas()
    {
        var payload = Obj(
            ("model", "local"),
            ("input", "run"),
            ("tools", List(
                Obj(
                    ("type", "function"),
                    ("name", "lookup"),
                    ("description", "Lookup data"),
                    ("parameters", Obj(
                        ("type", "object"),
                        ("properties", Obj(("q", Obj(("type", "string")))))))),
                Obj(("type", "web_search")),
                Obj(("type", "apply_patch"), ("description", "Patch files")))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "messages", "claude");

        Assert.Equal("claude", result["model"]);
        var tools = AssertList(result["tools"]).Select(AssertObject).ToList();
        Assert.Equal(
            [
                "lookup",
                "web_search",
                "apply_patch_add_file",
                "apply_patch_delete_file",
                "apply_patch_update_file",
                "apply_patch_replace_file",
                "apply_patch_batch"
            ],
            tools.Select(tool => tool["name"]).ToList());

        Assert.Equal("Lookup data", tools[0]["description"]);
        var lookupSchema = AssertObject(tools[0]["input_schema"]);
        Assert.True(AssertObject(lookupSchema["properties"]).ContainsKey("q"));

        var webSearchSchema = AssertObject(tools[1]["input_schema"]);
        Assert.Equal(false, webSearchSchema["additionalProperties"]);
        Assert.Equal(List("query"), AssertList(webSearchSchema["required"]));

        var updateSchema = AssertObject(tools[4]["input_schema"]);
        Assert.Equal(List("path", "hunks"), AssertList(updateSchema["required"]));
        Assert.True(AssertObject(updateSchema["properties"]).ContainsKey("hunks"));
    }

    [Fact]
    public void ChatMixedContentBlocksConvertToResponsesInputBlocks()
    {
        var imageBlock = Obj(("type", "input_image"), ("image_url", "https://example.test/a.png"));
        var payload = Obj(
            ("model", "local"),
            ("messages", List(Obj(
                ("role", "user"),
                ("content", List(
                    Obj(("type", "text"), ("text", "look")),
                    imageBlock))))));

        var result = ProtocolConverter.ConvertRequest(payload, "chat", "responses", "local");

        var input = AssertList(result["input"]);
        var message = AssertObject(input[0]);
        Assert.Equal("message", message["type"]);
        Assert.Equal("user", message["role"]);
        var content = AssertList(message["content"]);
        var textBlock = AssertObject(content[0]);
        Assert.Equal("input_text", textBlock["type"]);
        Assert.Equal("look", textBlock["text"]);
        var preservedImage = AssertObject(content[1]);
        Assert.Equal("input_image", preservedImage["type"]);
        Assert.Equal("https://example.test/a.png", preservedImage["image_url"]);
    }

    [Fact]
    public void MessagesToolResultContentConvertsToChatTextBlock()
    {
        var payload = Obj(
            ("model", "claude"),
            ("messages", List(Obj(
                ("role", "user"),
                ("content", List(
                    Obj(("type", "text"), ("text", "question")),
                    Obj(
                        ("type", "tool_result"),
                        ("tool_use_id", "tool_1"),
                        ("content", List(
                            Obj(("type", "text"), ("text", "tool answer")),
                            Obj(("type", "text"), ("text", " more")))))))))));

        var result = ProtocolConverter.ConvertRequest(payload, "messages", "chat", "upstream");

        var messages = AssertList(result["messages"]);
        var message = AssertObject(messages[0]);
        Assert.Equal("user", message["role"]);
        var content = AssertList(message["content"]);
        Assert.Equal("question", AssertObject(content[0])["text"]);
        Assert.Equal("tool answer more", AssertObject(content[1])["text"]);
    }

    [Fact]
    public void ResponsesApplyPatchRawInputIsWrappedForChat()
    {
        const string patch = "*** Begin Patch\n*** End Patch";
        var payload = Obj(
            ("model", "local"),
            ("input", List(Obj(
                ("type", "custom_tool_call"),
                ("call_id", "call_patch"),
                ("name", "apply_patch"),
                ("input", patch)))));

        var result = ProtocolConverter.ConvertRequest(payload, "responses", "chat", "upstream");

        var assistant = AssertObject(AssertList(result["messages"])[0]);
        var toolCall = AssertObject(AssertList(assistant["tool_calls"])[0]);
        var function = AssertObject(toolCall["function"]);
        Assert.Equal("apply_patch", function["name"]);
        using var document = JsonDocument.Parse(Assert.IsType<string>(function["arguments"]));
        Assert.Equal(patch, document.RootElement.GetProperty("patch").GetString());
    }

    [Fact]
    public void SameProtocolRequestDeepCopiesAndRewritesModel()
    {
        var payload = Obj(
            ("model", "local"),
            ("messages", List(Obj(("role", "user"), ("content", "ping")))));

        var result = ProtocolConverter.ConvertRequest(payload, "chat", "chat", "upstream");

        Assert.Equal("upstream", result["model"]);
        Assert.Equal("local", payload["model"]);
        Assert.NotSame(payload["messages"], result["messages"]);
    }

    [Fact]
    public void ChatResponseConvertsToResponses()
    {
        var toolCall = Obj(
            ("id", "call_1"),
            ("type", "function"),
            ("function", Obj(("name", "lookup"), ("arguments", "{\"q\":\"x\"}"))));
        var message = Obj(
            ("role", "assistant"),
            ("content", "hello"),
            ("tool_calls", List(toolCall)));
        var choice = Obj(
            ("message", message),
            ("finish_reason", "tool_calls"));
        var payload = Obj(
            ("id", "chatcmpl_1"),
            ("model", "upstream"),
            ("choices", List(choice)),
            ("usage", Obj(("prompt_tokens", 1), ("completion_tokens", 2), ("total_tokens", 3))));

        var result = ProtocolConverter.ConvertResponse(payload, "responses", "chat", "local");

        Assert.Equal("local", result["model"]);
        var output = AssertList(result["output"]);
        var outputMessage = AssertObject(output[0]);
        var content = AssertList(outputMessage["content"]);
        Assert.Equal("hello", AssertObject(content[0])["text"]);
        Assert.Equal("lookup", AssertObject(output[1])["name"]);
        Assert.Equal(3, AssertObject(result["usage"])["total_tokens"]);
    }

    [Fact]
    public void ChatResponseUsageWithoutTotalTokensFillsResponsesTotalTokens()
    {
        var payload = Obj(
            ("id", "chatcmpl_usage"),
            ("model", "upstream"),
            ("choices", List(Obj(
                ("message", Obj(("role", "assistant"), ("content", "ok"))),
                ("finish_reason", "stop")))),
            ("usage", Obj(("prompt_tokens", 4), ("completion_tokens", 6))));

        var result = ProtocolConverter.ConvertResponse(payload, "responses", "chat", "local");

        var usage = AssertObject(result["usage"]);
        Assert.Equal(4, usage["input_tokens"]);
        Assert.Equal(6, usage["output_tokens"]);
        Assert.Equal(10, usage["total_tokens"]);
    }

    [Fact]
    public void ChatResponseWithToolCallsOnlyConvertsReasoningAndFunctionCallToResponses()
    {
        var toolCall = Obj(
            ("id", "call_2"),
            ("type", "function"),
            ("function", Obj(("name", "exec_command"), ("arguments", "{\"cmd\":\"pwd\"}"))));
        var message = Obj(
            ("role", "assistant"),
            ("content", ""),
            ("reasoning_content", "need tool"),
            ("tool_calls", List(toolCall)));
        var choice = Obj(
            ("message", message),
            ("finish_reason", "tool_calls"));
        var payload = Obj(
            ("id", "chatcmpl_2"),
            ("model", "upstream"),
            ("choices", List(choice)),
            ("usage", Obj(("prompt_tokens", 2), ("completion_tokens", 3), ("total_tokens", 5))));

        var result = ProtocolConverter.ConvertResponse(payload, "responses", "chat", "visible-model");

        Assert.Equal("visible-model", result["model"]);
        var output = AssertList(result["output"]);
        Assert.Equal(2, output.Count);
        var reasoning = AssertObject(output[0]);
        Assert.Equal("reasoning", reasoning["type"]);
        Assert.Equal("need tool", AssertObject(AssertList(reasoning["summary"])[0])["text"]);
        Assert.Equal("need tool", reasoning["encrypted_content"]);
        var functionCall = AssertObject(output[1]);
        Assert.Equal("function_call", functionCall["type"]);
        Assert.Equal("call_2", functionCall["call_id"]);
        Assert.Equal("exec_command", functionCall["name"]);
        Assert.Equal(5, AssertObject(result["usage"])["total_tokens"]);
    }

    [Fact]
    public void ChatResponseLengthAndAnnotationsMapToResponsesIncomplete()
    {
        var annotation = Obj(
            ("type", "url_citation"),
            ("url", "https://example.test/a"),
            ("title", "Example"),
            ("summary", "snippet"));
        var message = Obj(
            ("role", "assistant"),
            ("content", "truncated"),
            ("annotations", List(annotation)));
        var choice = Obj(
            ("message", message),
            ("finish_reason", "length"));
        var payload = Obj(
            ("id", "chatcmpl_4"),
            ("model", "upstream"),
            ("choices", List(choice)));

        var result = ProtocolConverter.ConvertResponse(payload, "responses", "chat", "local");

        Assert.Equal("incomplete", result["status"]);
        Assert.Equal("max_output_tokens", AssertObject(result["incomplete_details"])["reason"]);
        var output = AssertList(result["output"]);
        var content = AssertList(AssertObject(output[0])["content"]);
        var annotations = AssertList(AssertObject(content[0])["annotations"]);
        var resultAnnotation = AssertObject(annotations[0]);
        Assert.Equal("url_citation", resultAnnotation["type"]);
        Assert.Equal("https://example.test/a", resultAnnotation["url"]);
        Assert.Equal("Example", resultAnnotation["title"]);
        Assert.Equal("snippet", resultAnnotation["snippet"]);
    }

    [Fact]
    public void MessagesResponseConvertsToChat()
    {
        var payload = Obj(
            ("id", "msg_1"),
            ("type", "message"),
            ("role", "assistant"),
            ("model", "claude"),
            ("content", List(Obj(("type", "text"), ("text", "ok")))),
            ("stop_reason", "end_turn"),
            ("usage", Obj(("input_tokens", 3), ("output_tokens", 4))));

        var result = ProtocolConverter.ConvertResponse(payload, "chat", "messages", "local");

        Assert.Equal("local", result["model"]);
        var choice = AssertObject(AssertList(result["choices"])[0]);
        var message = AssertObject(choice["message"]);
        Assert.Equal("ok", message["content"]);
        Assert.Equal(7, AssertObject(result["usage"])["total_tokens"]);
    }

    [Fact]
    public void UnsupportedRequestProtocolRaisesBadRequestMessage()
    {
        var payload = Obj(("model", "local"), ("input", "ping"));

        var exception = Assert.Throws<global::OpenCodex.Api.Errors.BadRequestException>(() =>
            ProtocolConverter.ConvertRequest(payload, "unknown", "chat", "upstream"));

        Assert.Equal("unsupported source protocol: unknown", exception.Message);
    }

    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    private static List<object?> List(params object?[] values)
    {
        return values.ToList();
    }

    private static Dictionary<string, object?> AssertObject(object? value)
    {
        return Assert.IsType<Dictionary<string, object?>>(value);
    }

    private static List<object?> AssertList(object? value)
    {
        return Assert.IsType<List<object?>>(value);
    }
}
