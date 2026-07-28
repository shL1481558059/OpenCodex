using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProtocolStructuralCompatibilityTests
{
    [Fact]
    public void ResponsesToChat_ConvertsSupportedParametersWithoutLeakingResponsesOnlyFields()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["include"] = new List<object?> { "reasoning.encrypted_content" },
                ["reasoning"] = new Dictionary<string, object?> { ["effort"] = "high" },
                ["text"] = new Dictionary<string, object?> { ["format"] = new Dictionary<string, object?> { ["type"] = "text" } },
                ["truncation"] = "auto",
                ["max_output_tokens"] = 100
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        Assert.Equal(100, converted["max_tokens"]);
        Assert.Equal("high", converted["reasoning_effort"]);
        Assert.Equal("text", String(Object(converted["response_format"]), "type"));
        foreach (var key in new[] { "include", "reasoning", "text", "truncation", "max_output_tokens" })
        {
            Assert.False(converted.ContainsKey(key), key);
        }
    }

    [Theory]
    [InlineData("background", true)]
    [InlineData("conversation", "conv_1")]
    [InlineData("previous_response_id", "resp_1")]
    [InlineData("prompt", "pmpt_1")]
    public void ResponsesToChat_StatefulParametersWithoutEquivalent_AreRejected(string key, object value)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["input"] = "hello",
            [key] = value
        };

        var exception = Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            request,
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream"));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesToMessages_ConvertsTextFormatToOutputConfig()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["text"] = new Dictionary<string, object?>
                {
                    ["format"] = new Dictionary<string, object?>
                    {
                        ["type"] = "json_schema",
                        ["name"] = "answer",
                        ["schema"] = new Dictionary<string, object?> { ["type"] = "object" },
                        ["strict"] = true
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        var outputConfig = Object(converted["output_config"]);
        Assert.Equal("json_schema", String(Object(outputConfig["format"]), "type"));
    }

    /// <summary>
    /// 复现真实会话 dde2878a519e：codex 的 Responses 请求里 reasoning 与并行
    /// function_call_output 结构完整，转成 Anthropic Messages 后不应退化成
    /// 空 content 消息、也不应把成组的 tool_result 打散成多条 user 消息。
    /// </summary>
    private static Dictionary<string, object?> CodexReasoningHistoryRequest()
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "帮我改造激活码绑定" }
                    }
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "reasoning",
                    ["summary"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "summary_text",
                            ["text"] = "先并行核查两个关键路径，再给方案。"
                        }
                    },
                    ["status"] = "completed"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "先确认路径。" }
                    }
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = "call_a",
                    ["name"] = "exec_command",
                    ["arguments"] = "{\"cmd\":\"ls a\"}"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = "call_b",
                    ["name"] = "exec_command",
                    ["arguments"] = "{\"cmd\":\"ls b\"}"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = "call_a",
                    ["output"] = "out a"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = "call_b",
                    ["output"] = "out b"
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "开始啊" }
                    }
                }
            }
        };
    }

    [Fact]
    public void ResponsesToMessages_WithPreserveThinkingHistory_KeepsReasoningSummaryAsTextBlock()
    {
        var converted = ConvertCodexReasoningHistory(preserveThinkingHistory: true);

        var reasoningBlock = Assert.Single(
            AnthropicTextBlocks(converted),
            text => text.Contains("先并行核查两个关键路径", StringComparison.Ordinal));
        Assert.StartsWith("<previous_thinking>", reasoningBlock, StringComparison.Ordinal);
        Assert.EndsWith("</previous_thinking>", reasoningBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesToMessages_WithPreserveThinkingHistory_DoesNotEmitEmptyContentMessages()
    {
        var converted = ConvertCodexReasoningHistory(preserveThinkingHistory: true);

        var empty = List(converted, "messages")
            .Select(Object)
            .Where(message => message["content"] is List<object?> { Count: 0 })
            .ToList();

        Assert.Empty(empty);
    }

    [Fact]
    public void ResponsesToMessages_WithPreserveThinkingHistory_DoesNotRequestNativeThinking()
    {
        var converted = ConvertCodexReasoningHistory(preserveThinkingHistory: true);

        // 降级为文本块时不能伪造原生 thinking 请求，签名缺失会被上游拒绝。
        Assert.False(converted.ContainsKey("thinking"));
        var serialized = System.Text.Json.JsonSerializer.Serialize(converted["messages"]);
        Assert.DoesNotContain("\"type\":\"thinking\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesToMessages_WithoutPreserveThinkingHistory_DropsReasoningSummary()
    {
        var converted = ConvertCodexReasoningHistory(preserveThinkingHistory: false);

        Assert.DoesNotContain(
            AnthropicTextBlocks(converted),
            text => text.Contains("先并行核查两个关键路径", StringComparison.Ordinal));
    }

    private static List<string> AnthropicTextBlocks(Dictionary<string, object?> converted)
    {
        return List(converted, "messages")
            .Select(Object)
            .Where(message => message["content"] is List<object?>)
            .SelectMany(message => Assert.IsType<List<object?>>(message["content"]))
            .Select(Object)
            .Where(block => block.TryGetValue("type", out var type) && (string?)type == "text")
            .Select(block => String(block, "text"))
            .ToList();
    }

    private static Dictionary<string, object?> ConvertCodexReasoningHistory(bool preserveThinkingHistory)
    {
        var compat = new Dictionary<string, object?>
        {
            ["preserve_thinking_history"] = preserveThinkingHistory
        };

        var rewritten = ChannelCompatRequestRewriter.Apply(CodexReasoningHistoryRequest(), compat).Payload;
        return ProtocolConverter.ConvertRequest(
            rewritten,
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream",
            compat);
    }

    [Fact]
    public void MessagesToResponses_ConvertsOutputConfigToTextFormat()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["max_tokens"] = 100,
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "hello" }
                },
                ["output_config"] = new Dictionary<string, object?>
                {
                    ["format"] = new Dictionary<string, object?> { ["type"] = "json_schema", ["name"] = "answer" }
                }
            },
            ProtocolConverter.Messages,
            ProtocolConverter.Responses,
            "upstream");

        var text = Object(converted["text"]);
        Assert.Equal("json_schema", String(Object(text["format"]), "type"));
    }

    [Theory]
    [InlineData("responses", "messages", "reasoning")]
    [InlineData("responses", "messages", "parallel_tool_calls")]
    [InlineData("messages", "responses", "container")]
    [InlineData("messages", "chat", "thinking")]
    [InlineData("chat", "messages", "reasoning_effort")]
    [InlineData("chat", "messages", "parallel_tool_calls")]
    public void RequestParametersThatChangeStateOrModelBehavior_AreRejectedWhenNoEquivalentExists(
        string source,
        string target,
        string key)
    {
        var request = RequestForProtocol(source);
        request[key] = key switch
        {
            "reasoning" => new Dictionary<string, object?> { ["effort"] = "high" },
            "thinking" => new Dictionary<string, object?> { ["type"] = "enabled", ["budget_tokens"] = 1024 },
            "parallel_tool_calls" => false,
            "reasoning_effort" => "high",
            _ => "state_1"
        };

        var exception = Assert.Throws<BadRequestException>(() => ProtocolConverter.ConvertRequest(
            request,
            source,
            target,
            "upstream"));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesToMessages_WithoutMaxOutputTokens_UsesCompatibilityDefault()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello"
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        Assert.Equal(4096, converted["max_tokens"]);
    }

    [Fact]
    public void MessagesToChat_PreservesToolUseAndToolResultHistory()
    {
        var converted = ProtocolConverter.ConvertRequest(
            MessagesToolHistoryRequest(),
            ProtocolConverter.Messages,
            ProtocolConverter.Chat,
            "upstream");

        var messages = List(converted, "messages").Select(Object).ToList();
        var assistant = Assert.Single(messages, message => String(message, "role") == "assistant");
        var toolCall = Object(Assert.Single(List(assistant, "tool_calls")));
        Assert.Equal("toolu_1", String(toolCall, "id"));
        Assert.Equal("lookup", String(Object(toolCall["function"]), "name"));

        var tool = Assert.Single(messages, message => String(message, "role") == "tool");
        Assert.Equal("toolu_1", String(tool, "tool_call_id"));
        Assert.Equal("result", String(tool, "content"));
    }

    [Fact]
    public void MessagesToResponses_PreservesToolUseAndToolResultHistory()
    {
        var request = MessagesToolHistoryRequest();
        request["max_tokens"] = 200;
        var converted = ProtocolConverter.ConvertRequest(
            request,
            ProtocolConverter.Messages,
            ProtocolConverter.Responses,
            "upstream");

        var input = List(converted, "input").Select(Object).ToList();
        var call = Assert.Single(input, item => String(item, "type") == "function_call");
        Assert.Equal("toolu_1", String(call, "call_id"));
        Assert.Equal("lookup", String(call, "name"));
        var output = Assert.Single(input, item => String(item, "type") == "function_call_output");
        Assert.Equal("toolu_1", String(output, "call_id"));
        Assert.Equal("result", String(output, "output"));
    }

    [Fact]
    public void ChatToResponses_ConvertsImageUrlToInputImage()
    {
        var converted = ProtocolConverter.ConvertRequest(
            ChatImageRequest(), ProtocolConverter.Chat, ProtocolConverter.Responses, "upstream");
        var message = Object(Assert.Single(List(converted, "input")));
        var image = Object(Assert.Single(List(message, "content"), item => String(Object(item), "type") == "input_image"));
        Assert.Equal("https://example.test/image.png", String(image, "image_url"));
        Assert.Equal("high", String(image, "detail"));
    }

    [Fact]
    public void ChatToMessages_ConvertsImageUrlToAnthropicImageSource()
    {
        var request = ChatImageRequest();
        request["max_tokens"] = 100;
        var converted = ProtocolConverter.ConvertRequest(
            request, ProtocolConverter.Chat, ProtocolConverter.Messages, "upstream");
        var message = Object(Assert.Single(List(converted, "messages")));
        var image = Object(Assert.Single(List(message, "content"), item => String(Object(item), "type") == "image"));
        var source = Object(image["source"]);
        Assert.Equal("url", String(source, "type"));
        Assert.Equal("https://example.test/image.png", String(source, "url"));
    }

    [Fact]
    public void MessagesToChat_ConvertsImageSourceToImageUrl()
    {
        var converted = ProtocolConverter.ConvertRequest(
            MessagesImageRequest(), ProtocolConverter.Messages, ProtocolConverter.Chat, "upstream");
        var message = Object(Assert.Single(List(converted, "messages")));
        var image = Object(Assert.Single(List(message, "content"), item => String(Object(item), "type") == "image_url"));
        Assert.Equal("https://example.test/image.png", String(Object(image["image_url"]), "url"));
    }

    [Theory]
    [InlineData("completed", false, "stop", "end_turn")]
    [InlineData("completed", true, "tool_calls", "tool_use")]
    [InlineData("incomplete", false, "length", "max_tokens")]
    public void ResponsesStatus_IsMappedToTargetFinishReasons(
        string status,
        bool withTool,
        string expectedChat,
        string expectedMessages)
    {
        var response = ResponsesResponse(status, withTool);
        var chat = ProtocolConverter.ConvertResponse(response, ProtocolConverter.Chat, ProtocolConverter.Responses, "public");
        var chatChoice = Object(Assert.Single(List(chat, "choices")));
        Assert.Equal(expectedChat, String(chatChoice, "finish_reason"));

        var messages = ProtocolConverter.ConvertResponse(response, ProtocolConverter.Messages, ProtocolConverter.Responses, "public");
        Assert.Equal(expectedMessages, String(messages, "stop_reason"));
    }

    [Fact]
    public void ResponsesNamedFunctionChoice_MapsToChatNamedFunctionChoice()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["name"] = "lookup",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>()
                        }
                    }
                },
                ["tool_choice"] = new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["name"] = "lookup"
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var choice = Object(converted["tool_choice"]);
        Assert.Equal("function", String(choice, "type"));
        Assert.Equal("lookup", String(Object(choice["function"]), "name"));
    }

    [Theory]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Chat)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Messages)]
    [InlineData(ProtocolConverter.Responses, ProtocolConverter.Responses)]
    [InlineData(ProtocolConverter.Chat, ProtocolConverter.Chat)]
    public void ConvertRequest_EmptyToolsWithToolChoice_DropsToolChoice(
        string sourceProtocol,
        string targetProtocol)
    {
        Dictionary<string, object?> payload = sourceProtocol switch
        {
            ProtocolConverter.Responses => new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["tools"] = new List<object?>(),
                ["tool_choice"] = "auto"
            },
            ProtocolConverter.Chat => new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = "hello"
                    }
                },
                ["tools"] = new List<object?>(),
                ["tool_choice"] = "auto"
            },
            _ => throw new InvalidOperationException(sourceProtocol)
        };

        var converted = ProtocolConverter.ConvertRequest(
            payload,
            sourceProtocol,
            targetProtocol,
            "upstream");

        Assert.False(converted.ContainsKey("tools"));
        Assert.False(converted.ContainsKey("tool_choice"));
    }

    [Fact]
    public void ConvertRequest_ResponsesToChat_KeepsToolChoiceWhenToolsPresent()
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["name"] = "lookup",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>()
                        }
                    }
                },
                ["tool_choice"] = "auto"
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        Assert.Equal("auto", converted["tool_choice"]);
        var tools = Assert.IsType<List<object?>>(converted["tools"]);
        Assert.Single(tools);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("required")]
    public void ConvertRequest_ResponsesToChat_DropsStringToolChoiceWithoutTools(string toolChoice)
    {
        var converted = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello",
                ["tools"] = new List<object?>(),
                ["tool_choice"] = toolChoice
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        Assert.False(converted.ContainsKey("tools"));
        Assert.False(converted.ContainsKey("tool_choice"));
    }

    private static Dictionary<string, object?> MessagesToolHistoryRequest()
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["max_tokens"] = 200,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = "toolu_1",
                            ["name"] = "lookup",
                            ["input"] = new Dictionary<string, object?> { ["query"] = "x" }
                        }
                    }
                },
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = "toolu_1",
                            ["content"] = "result"
                        }
                    }
                }
            }
        };
    }

    private static Dictionary<string, object?> ChatImageRequest()
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?>
                            {
                                ["url"] = "https://example.test/image.png",
                                ["detail"] = "high"
                            }
                        }
                    }
                }
            }
        };
    }

    private static Dictionary<string, object?> MessagesImageRequest()
    {
        return new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["max_tokens"] = 100,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "image",
                            ["source"] = new Dictionary<string, object?>
                            {
                                ["type"] = "url",
                                ["url"] = "https://example.test/image.png"
                            }
                        }
                    }
                }
            }
        };
    }

    private static Dictionary<string, object?> RequestForProtocol(string protocol)
    {
        return protocol switch
        {
            ProtocolConverter.Responses => new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["input"] = "hello"
            },
            ProtocolConverter.Chat => new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "hello" }
                }
            },
            ProtocolConverter.Messages => new Dictionary<string, object?>
            {
                ["model"] = "public",
                ["max_tokens"] = 100,
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "hello" }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
    }

    private static Dictionary<string, object?> ResponsesResponse(string status, bool withTool)
    {
        var output = new List<object?>();
        if (withTool)
        {
            output.Add(new Dictionary<string, object?>
            {
                ["type"] = "function_call",
                ["id"] = "fc_1",
                ["call_id"] = "call_1",
                ["name"] = "lookup",
                ["arguments"] = "{}"
            });
        }
        else
        {
            output.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = "done" }
                }
            });
        }

        var response = new Dictionary<string, object?>
        {
            ["id"] = "resp_1",
            ["model"] = "upstream",
            ["created_at"] = 1,
            ["status"] = status,
            ["output"] = output,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = 1,
                ["output_tokens"] = 1,
                ["total_tokens"] = 2
            }
        };
        if (status == "incomplete")
        {
            response["incomplete_details"] = new Dictionary<string, object?> { ["reason"] = "max_output_tokens" };
        }

        return response;
    }

    private static Dictionary<string, object?> Object(object? value) => Assert.IsType<Dictionary<string, object?>>(value);
    private static List<object?> List(Dictionary<string, object?> value, string key) => Assert.IsType<List<object?>>(value[key]);
    private static string String(Dictionary<string, object?> value, string key) => Assert.IsType<string>(value[key]);
}
