using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Models;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class CanonicalModelTests
{
    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void FailedResponse_PreservesErrorAcrossCanonicalSerialization(ProtocolKind target)
    {
        var facade = new ProtocolConversionFacade();
        var payload = Payload("""
            {"id":"r","model":"m","status":"failed","error":{"type":"rate_limit_exceeded","message":"slow down"},"output":[]}
            """);
        var canonical = facade.ToCanonicalResponse(payload, ProtocolKind.Responses);
        var restored = JsonSerializer.Deserialize<CanonicalResponse>(JsonSerializer.Serialize(canonical))!;

        var result = facade.ConvertResponse(restored, target, ProtocolKind.Responses);

        var error = Assert.IsType<Dictionary<string, object?>>(result["error"]);
        Assert.Equal("rate_limit_exceeded", error["type"]);
        Assert.Equal("slow down", error["message"]);
        Assert.DoesNotContain("choices", result.Keys);
        if (target == ProtocolKind.Responses) Assert.Equal("failed", result["status"]);
        if (target == ProtocolKind.Messages) Assert.Equal("error", result["type"]);
    }

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Responses)]
    [InlineData(ProtocolKind.Messages)]
    public void ImageRequest_PreservesImageAcrossCanonicalSerialization(ProtocolKind target)
    {
        var facade = new ProtocolConversionFacade();
        var payload = Payload("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"image_url","image_url":{"url":"https://example.test/image.png","detail":"high"}}
            ]}]}
            """);
        var canonical = facade.ToCanonicalRequest(payload, ProtocolKind.Chat);
        var image = Assert.Single(Assert.Single(canonical.Messages).Content);
        Assert.Equal("https://example.test/image.png", image.ImageUrl);
        Assert.Equal("high", image.Detail);
        var restored = JsonSerializer.Deserialize<CanonicalRequest>(JsonSerializer.Serialize(canonical))!;

        var result = facade.ConvertRequest(restored, target, "m");
        var items = Assert.IsType<List<object?>>(result[target == ProtocolKind.Responses ? "input" : "messages"]);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(items));
        var content = Assert.IsType<List<object?>>(message["content"]);
        var part = Assert.IsType<Dictionary<string, object?>>(Assert.Single(content));
        if (target == ProtocolKind.Chat)
        {
            var source = Assert.IsType<Dictionary<string, object?>>(part["image_url"]);
            Assert.Equal("https://example.test/image.png", source["url"]);
            Assert.Equal("high", source["detail"]);
        }
        else if (target == ProtocolKind.Responses)
        {
            Assert.Equal("https://example.test/image.png", part["image_url"]);
            Assert.Equal("high", part["detail"]);
        }
        else
        {
            var source = Assert.IsType<Dictionary<string, object?>>(part["source"]);
            Assert.Equal("https://example.test/image.png", source["url"]);
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Chat)]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void RequestModel_WithAbsentOptionalFields_Serializes(ProtocolKind source)
    {
        var payload = source switch
        {
            ProtocolKind.Chat => Payload("""{"model":"m","messages":[{"role":"user","content":"hello"}]}"""),
            ProtocolKind.Messages => Payload("""{"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hello"}]}"""),
            _ => Payload("""{"model":"m","input":"hello"}""")
        };
        var facade = new ProtocolConversionFacade();
        var canonical = facade.ToCanonicalRequest(payload, source);

        var restored = JsonSerializer.Deserialize<CanonicalRequest>(JsonSerializer.Serialize(canonical))!;
        var result = facade.ConvertRequest(restored, source, "m");

        Assert.Equal("m", result["model"]);
        Assert.Equal("hello", Assert.Single(Assert.Single(restored.Messages).Content).Text);
    }

    [Fact]
    public void SuccessfulResponse_WithAbsentError_Serializes()
    {
        var canonical = new ProtocolConversionFacade().ToCanonicalResponse(
            Payload("""{"id":"r","model":"m","status":"completed","output":[]}"""),
            ProtocolKind.Responses);

        var restored = JsonSerializer.Deserialize<CanonicalResponse>(JsonSerializer.Serialize(canonical))!;

        Assert.Equal("r", restored.Id);
        Assert.True(restored.Error is null || restored.Error.Value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void CrossProtocolRequest_ConvertsKnownParametersAndFiltersSourceFields()
    {
        var facade = new ProtocolConversionFacade();
        var canonical = facade.ToCanonicalRequest(Payload("""
            {"model":"m","input":"hello","reasoning":{"effort":"high"},"text":{"format":{"type":"text"}},"temperature":0.2}
            """), ProtocolKind.Responses);

        var result = facade.ConvertRequest(canonical, ProtocolKind.Chat, "m");

        Assert.Equal("high", result["reasoning_effort"]);
        Assert.Equal("text", Assert.IsType<Dictionary<string, object?>>(result["response_format"])["type"]);
        Assert.Equal(0.2, result["temperature"]);
        Assert.DoesNotContain("reasoning", result.Keys);
        Assert.DoesNotContain("text", result.Keys);
    }

    [Fact]
    public void SameProtocolRequest_PreservesExtensionsAfterCanonicalSerialization()
    {
        var facade = new ProtocolConversionFacade();
        var canonical = facade.ToCanonicalRequest(Payload("""
            {"model":"m","messages":[{"role":"user","content":"hello"}],"vendor_flag":true}
            """), ProtocolKind.Chat);
        var restored = JsonSerializer.Deserialize<CanonicalRequest>(JsonSerializer.Serialize(canonical))!;

        var result = facade.ConvertRequest(restored, ProtocolKind.Chat, "m");

        Assert.Equal(true, result["vendor_flag"]);
    }

    private static Dictionary<string, object?> Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)ProtocolJson.FromJsonElement(document.RootElement)!;
    }

    [Fact]
    public void ChatRequest_RoundTripsTypedContentToolsAndExtensions()
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "public",
            ["temperature"] = 0.2,
            ["vendor_flag"] = true,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = "hello",
                            ["vendor_part"] = 7
                        }
                    },
                    ["vendor_message"] = "kept"
                }
            },
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "lookup",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["query"] = new Dictionary<string, object?> { ["type"] = "string" }
                            }
                        }
                    }
                }
            }
        };

        var canonical = new ProtocolConversionFacade().ToCanonicalRequest(payload, ProtocolKind.Chat);
        var message = Assert.Single(canonical.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("hello", Assert.Single(message.Content).Text);
        Assert.True(message.ExtensionData!.ContainsKey("vendor_message"));
        Assert.Equal(2, canonical.Parameters!.Count);
        Assert.Single(canonical.Tools);

        var restored = new ProtocolConversionFacade().ConvertRequest(
            canonical,
            ProtocolKind.Chat,
            "public");
        Assert.Equal(0.2, Assert.IsType<double>(restored["temperature"]));
        Assert.True(Assert.IsType<bool>(restored["vendor_flag"]));
        Assert.True(restored.ContainsKey("messages"));
    }

    [Fact]
    public void ChatResponse_RoundTripsToolCallsUsageAndUnknownFields()
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["object"] = "chat.completion",
            ["created"] = 123,
            ["model"] = "upstream",
            ["system_fingerprint"] = "fp_1",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["finish_reason"] = "tool_calls",
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = "checking",
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = "call_1",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = "lookup",
                                    ["arguments"] = "{\"query\":\"x\"}"
                                }
                            }
                        }
                    }
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 5,
                ["completion_tokens"] = 7,
                ["total_tokens"] = 12,
                ["prompt_tokens_details"] = new Dictionary<string, object?>
                {
                    ["cached_tokens"] = 3,
                    ["audio_tokens"] = 2
                }
            }
        };

        var facade = new ProtocolConversionFacade();
        var canonical = facade.ToCanonicalResponse(payload, ProtocolKind.Chat, "public");
        Assert.Equal("public", canonical.Model);
        Assert.Equal("tool_calls", canonical.FinishReason);
        Assert.Equal(5, canonical.Usage.InputTokens);
        Assert.Equal(7, canonical.Usage.OutputTokens);
        Assert.Equal(3, canonical.Usage.CachedTokens);
        Assert.Single(canonical.ToolCalls);
        Assert.Equal(JsonValueKind.Object, canonical.Raw!.Value.ValueKind);

        var restored = facade.ConvertResponse(canonical, ProtocolKind.Chat, ProtocolKind.Chat);
        Assert.Equal("public", restored["model"]);
        Assert.Equal("fp_1", restored["system_fingerprint"]);
        var restoredUsage = (Dictionary<string, object?>)restored["usage"]!;
        Assert.Equal(12, restoredUsage["total_tokens"]);
        var restoredDetails = Assert.IsType<Dictionary<string, object?>>(restoredUsage["prompt_tokens_details"]);
        Assert.Equal(3, restoredDetails["cached_tokens"]);
        Assert.Equal(2, restoredDetails["audio_tokens"]);

        var crossProtocol = facade.ConvertResponse(canonical, ProtocolKind.Responses, ProtocolKind.Chat);
        var crossUsage = Assert.IsType<Dictionary<string, object?>>(crossProtocol["usage"]);
        Assert.Equal(5, crossUsage["input_tokens"]);
        Assert.DoesNotContain("prompt_tokens_details", crossUsage.Keys);
        Assert.DoesNotContain("audio_tokens", JsonSerializer.Serialize(crossProtocol));
    }

    [Fact]
    public void RequestModel_PreservesSchemaAsJsonElement()
    {
        var model = new CanonicalRequest
        {
            Model = "model",
            Tools =
            [
                new CanonicalToolDefinition
                {
                    Name = "tool",
                    InputSchema = JsonDocument.Parse(
                        """{"type":"object","properties":{"x":{"type":"integer"}}}""").RootElement
                }
            ]
        };

        var graph = new ProtocolConversionFacade().ConvertRequest(
            model,
            ProtocolKind.Responses,
            "model");
        var tools = Assert.IsType<List<object?>>(graph["tools"]);
        var tool = Assert.IsType<Dictionary<string, object?>>(Assert.Single(tools));
        var parameters = Assert.IsType<Dictionary<string, object?>>(tool["parameters"]);
        Assert.Equal("object", parameters["type"]);
    }
}
