using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
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
