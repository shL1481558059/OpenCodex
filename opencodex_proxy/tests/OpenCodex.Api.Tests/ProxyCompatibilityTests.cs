using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyCompatibilityTests : IClassFixture<OpenCodexApiFactory>
{
    private readonly HttpClient _client;

    public ProxyCompatibilityTests(OpenCodexApiFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ResponsesProxy_DropToolTypes_StripsImageGenerationToolsOnly()
    {
        using var factory = new ProxyCompatibilityApiFactory(ResponsesTextResponse("done"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var cookie = await LoginAndReadSessionCookie(client);
        var config = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "responses",
                        name = "Responses",
                        type = "responses",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        timeout_seconds = 30,
                        retry_count = 0,
                        enabled = true,
                        models = new[]
                        {
                            new { model = "public-model", upstream_model = "upstream-model", supports_image = false }
                        },
                        compat = new
                        {
                            drop_tool_types = new[] { "image_generation", "image_generation_call" }
                        }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        var createdKey = await SendJsonWithCookie(
            client,
            HttpMethod.Post,
            "/api-keys",
            cookie,
            new { owner_username = "admin", name = "cli-drop-tools" });
        Assert.Equal(HttpStatusCode.Created, createdKey.StatusCode);
        var apiKey = await ReadStringProperty(createdKey, "Data", "key", "key");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "public-model",
                input = "draw then search",
                tools = new object[]
                {
                    new { type = "web_search" },
                    new { type = "image_generation", size = "1024x1024" },
                    new { type = "image_generation_call", quality = "low" },
                    new
                    {
                        type = "function",
                        name = "keep_tool",
                        parameters = new { type = "object", properties = new { } }
                    }
                },
                tool_choice = new { type = "image_generation" },
                include = new[] { "message.output_text", "image_generation_call.results", "reasoning.summary" },
                temperature = 0.2
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upstreamRequest = Assert.Single(factory.Upstream.Requests);
        Assert.False(upstreamRequest.ContainsKey("tool_choice"));
        Assert.Equal(0.2, Assert.IsType<double>(upstreamRequest["temperature"]));

        var tools = Assert.IsType<List<object?>>(upstreamRequest["tools"])
            .Select(item => Assert.IsType<Dictionary<string, object?>>(item))
            .ToArray();
        Assert.Equal(["web_search", "function"], tools.Select(tool => tool["type"]?.ToString() ?? string.Empty).ToArray());
        Assert.Equal("keep_tool", tools[1]["name"]);

        var include = Assert.IsType<List<object?>>(upstreamRequest["include"]);
        Assert.Equal(["message.output_text", "reasoning.summary"], include.Select(item => item?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void ChannelCompatRequestRewriter_DropToolTypes_RemovesEmptyToolsIncludeAndStringToolChoice()
    {
        var payload = new Dictionary<string, object?>
        {
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "image_generation" }
            },
            ["tool_choice"] = "image_generation_call",
            ["include"] = new List<object?> { "image_generation.results" },
            ["temperature"] = 0.2
        };
        var compat = new Dictionary<string, object?>
        {
            ["drop_tool_types"] = new List<object?> { "image_generation", "image_generation_call" }
        };

        var result = ChannelCompatRequestRewriter.Apply(payload, compat).Payload;

        Assert.False(result.ContainsKey("tools"));
        Assert.False(result.ContainsKey("tool_choice"));
        Assert.False(result.ContainsKey("include"));
        Assert.Equal(0.2, Assert.IsType<double>(result["temperature"]));
        Assert.True(payload.ContainsKey("tools"));
        Assert.True(payload.ContainsKey("tool_choice"));
        Assert.True(payload.ContainsKey("include"));
    }

    [Fact]
    public async Task ModelsEndpoint_ReturnsBearerUserMappedModels()
    {
        var cookie = await LoginAndReadSessionCookie();
        var config = await SendJsonWithCookie(
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new object[]
                {
                    new
                    {
                        id = "chat",
                        name = "Chat",
                        type = "chat",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        timeout_seconds = 30,
                        retry_count = 0,
                        enabled = true,
                        models = new[]
                        {
                            new { model = "public-model", upstream_model = "upstream-model", supports_image = true }
                        }
                    },
                    new
                    {
                        id = "disabled",
                        name = "Disabled",
                        type = "chat",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        timeout_seconds = 30,
                        retry_count = 0,
                        enabled = false,
                        models = new[]
                        {
                            new { model = "hidden-model", upstream_model = "hidden-upstream" }
                        }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        var createdKey = await SendJsonWithCookie(
            HttpMethod.Post,
            "/api-keys",
            cookie,
            new { owner_username = "admin", name = "cli" });
        Assert.Equal(HttpStatusCode.Created, createdKey.StatusCode);
        var apiKey = await ReadStringProperty(createdKey, "Data", "key", "key");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models?client_version=0.137.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("list", document.RootElement.GetProperty("object").GetString());
        var ids = document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["public-model"], ids);
        var catalogIds = document.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(item => item.GetProperty("slug").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["public-model"], catalogIds);
        var catalogItem = document.RootElement.GetProperty("models")[0];
        Assert.Equal("public-model", catalogItem.GetProperty("display_name").GetString());
        Assert.Equal("medium", catalogItem.GetProperty("default_reasoning_level").GetString());
        Assert.True(catalogItem.TryGetProperty("supported_reasoning_levels", out _));
        Assert.False(string.IsNullOrWhiteSpace(catalogItem.GetProperty("base_instructions").GetString()));
        Assert.True(catalogItem.TryGetProperty("model_messages", out _));
        Assert.True(catalogItem.GetProperty("supports_search_tool").GetBoolean());
        Assert.Equal("freeform", catalogItem.GetProperty("apply_patch_tool_type").GetString());
        Assert.True(catalogItem.TryGetProperty("experimental_supported_tools", out _));
        Assert.Equal(["text", "image"], catalogItem.GetProperty("input_modalities")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray());
        Assert.True(catalogItem.GetProperty("supports_image_detail_original").GetBoolean());

        var rootRequest = new HttpRequestMessage(HttpMethod.Get, "/models?client_version=0.137.0");
        rootRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var rootResponse = await _client.SendAsync(rootRequest);

        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        using var rootDocument = await JsonDocument.ParseAsync(await rootResponse.Content.ReadAsStreamAsync());
        Assert.Equal(["public-model"], rootDocument.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(item => item.GetProperty("slug").GetString() ?? string.Empty)
            .ToArray());
    }

    [Fact]
    public async Task ConfigEndpoint_PreservesSupportsImageAndModelsEndpointReportsPerModelCapability()
    {
        var cookie = await LoginAndReadSessionCookie();
        var config = await SendJsonWithCookie(
            HttpMethod.Post,
            "/config",
            cookie,
            new
            {
                channels = new[]
                {
                    new
                    {
                        id = "chat",
                        name = "Chat",
                        type = "chat",
                        baseurl = "https://example.test/v1",
                        apikey = "secret",
                        auth_mode = "config",
                        timeout_seconds = 30,
                        retry_count = 0,
                        enabled = true,
                        models = new[]
                        {
                            new { model = "text-model", upstream_model = "text-upstream", supports_image = false },
                            new { model = "vision-model", upstream_model = "vision-upstream", supports_image = true }
                        }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);

        var readConfig = await SendWithCookie(HttpMethod.Get, "/config", cookie);
        Assert.Equal(HttpStatusCode.OK, readConfig.StatusCode);
        using (var configDocument = await JsonDocument.ParseAsync(await readConfig.Content.ReadAsStreamAsync()))
        {
            var models = configDocument.RootElement
                .GetProperty("Data")
                .GetProperty("channels")[0]
                .GetProperty("models")
                .EnumerateArray()
                .ToArray();
            Assert.False(models[0].GetProperty("supports_image").GetBoolean());
            Assert.True(models[1].GetProperty("supports_image").GetBoolean());
        }

        var createdKey = await SendJsonWithCookie(
            HttpMethod.Post,
            "/api-keys",
            cookie,
            new { owner_username = "admin", name = "cli-vision" });
        Assert.Equal(HttpStatusCode.Created, createdKey.StatusCode);
        var apiKey = await ReadStringProperty(createdKey, "Data", "key", "key");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var modelsDocument = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var catalog = modelsDocument.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var textModel = catalog.Single(item => item.GetProperty("slug").GetString() == "text-model");
        var visionModel = catalog.Single(item => item.GetProperty("slug").GetString() == "vision-model");

        Assert.Equal(["text"], textModel.GetProperty("input_modalities")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray());
        Assert.False(textModel.GetProperty("supports_image_detail_original").GetBoolean());
        Assert.Equal(["text", "image"], visionModel.GetProperty("input_modalities")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray());
        Assert.True(visionModel.GetProperty("supports_image_detail_original").GetBoolean());
    }

    [Fact]
    public async Task ListModelsAsync_NormalizesArrayRootResponses()
    {
        var handler = new StaticJsonHandler(
            """
            [
              { "id": "model-a" },
              { "id": "model-b" }
            ]
            """);
        var upstream = new HttpUpstreamClient(new HttpClient(handler));

        var result = await upstream.ListModelsAsync(
            new Dictionary<string, object?>
            {
                ["type"] = "chat",
                ["baseurl"] = "https://example.test/v1",
                ["auth_mode"] = "none",
                ["retry_count"] = 0
            },
            30,
            CancellationToken.None);

        Assert.Equal("https://example.test/v1/models", handler.RequestUri?.ToString());
        var data = Assert.IsType<List<object?>>(result["data"]);
        Assert.Equal(["model-a", "model-b"], data
            .Select(item => Assert.IsType<Dictionary<string, object?>>(item)["id"]?.ToString() ?? string.Empty)
            .ToArray());
    }

    [Fact]
    public void ConvertRequest_ResponsesPlanModeTagInDeveloperInput_AppendsPlanInstruction()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["instructions"] = "base system",
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "developer",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_text",
                                ["text"] = "Use <proposed_plan> for official plans."
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
                                ["type"] = "input_text",
                                ["text"] = "plan it"
                            }
                        }
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var messages = Assert.IsType<List<object?>>(request["messages"]);
        var system = Assert.IsType<Dictionary<string, object?>>(messages[0]);
        Assert.Equal("system", system["role"]);
        var systemContent = Assert.IsType<string>(system["content"]);
        Assert.Contains("base system\n\nUse <proposed_plan>", systemContent);
        Assert.Contains("You are currently in Codex Plan Mode.", systemContent);
        Assert.Contains("</proposed_plan>", systemContent);

        var user = Assert.IsType<Dictionary<string, object?>>(messages[1]);
        Assert.Equal("user", user["role"]);
        Assert.Equal("plan it", user["content"]);
    }

    [Fact]
    public void ConvertRequest_ResponsesPlanModeTagInUserInput_DoesNotAppendPlanInstruction()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["instructions"] = "base system",
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_text",
                                ["text"] = "What does <proposed_plan> mean?"
                            }
                        }
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var messages = Assert.IsType<List<object?>>(request["messages"]);
        var system = Assert.IsType<Dictionary<string, object?>>(messages[0]);
        var systemContent = Assert.IsType<string>(system["content"]);
        Assert.Contains("base system", systemContent);
        Assert.DoesNotContain("You are currently in Codex Plan Mode.", systemContent);
        Assert.DoesNotContain("The client will not recognize the plan", systemContent);
    }

    [Fact]
    public void ConvertRequest_ResponsesInputImage_ConvertsToChatImageUrlContent()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_text",
                                ["text"] = "看看这张图"
                            },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_image",
                                ["image_url"] = "data:image/png;base64,AAAA",
                                ["detail"] = "high"
                            }
                        }
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var messages = Assert.IsType<List<object?>>(request["messages"]);
        var user = Assert.IsType<Dictionary<string, object?>>(messages[^1]);
        Assert.Equal("user", user["role"]);

        var content = Assert.IsType<List<object?>>(user["content"]);
        var textPart = Assert.IsType<Dictionary<string, object?>>(content[0]);
        Assert.Equal("text", textPart["type"]);
        Assert.Equal("看看这张图", textPart["text"]);

        var imagePart = Assert.IsType<Dictionary<string, object?>>(content[1]);
        Assert.Equal("image_url", imagePart["type"]);

        var imageUrl = Assert.IsType<Dictionary<string, object?>>(imagePart["image_url"]);
        Assert.Equal("data:image/png;base64,AAAA", imageUrl["url"]);
        Assert.Equal("high", imageUrl["detail"]);

        var serialized = JsonSerializer.Serialize(content);
        Assert.DoesNotContain("input_image", serialized);
    }

    [Fact]
    public void ConvertRequest_ResponsesToolSchemaWithEmptyStringEnum_SanitizesForChat()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["input"] = "ping",
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["name"] = "format_text",
                        ["description"] = "Format text.",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["formatting"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "object",
                                    ["properties"] = new Dictionary<string, object?>
                                    {
                                        ["link"] = new Dictionary<string, object?>
                                        {
                                            ["anyOf"] = new List<object?>
                                            {
                                                new Dictionary<string, object?>
                                                {
                                                    ["type"] = "string",
                                                    ["enum"] = new List<object?> { string.Empty }
                                                },
                                                new Dictionary<string, object?>
                                                {
                                                    ["type"] = "string"
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "upstream");

        var serialized = JsonSerializer.Serialize(request["tools"]);
        Assert.DoesNotContain("\"enum\":[\"\"]", serialized);
        Assert.Contains("\"link\":{\"type\":\"string\"}", serialized);
    }

    [Fact]
    public void ConvertRequest_ChatToolSchemaWithEmptyStringEnum_SanitizesForChat()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = "ping"
                    }
                },
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = "format_text",
                            ["parameters"] = EmptyStringEnumParameters()
                        }
                    }
                }
            },
            ProtocolConverter.Chat,
            ProtocolConverter.Chat,
            "upstream");

        var serialized = JsonSerializer.Serialize(request["tools"]);
        Assert.DoesNotContain("\"enum\":[\"\"]", serialized);
        Assert.Contains("\"link\":{\"type\":\"string\"}", serialized);
    }

    [Fact]
    public void ConvertRequest_ResponsesToolSchemaWithEmptyStringEnum_SanitizesForMessages()
    {
        var request = ProtocolConverter.ConvertRequest(
            new Dictionary<string, object?>
            {
                ["model"] = "local",
                ["input"] = "ping",
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["name"] = "format_text",
                        ["parameters"] = EmptyStringEnumParameters()
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Messages,
            "upstream");

        var serialized = JsonSerializer.Serialize(request["tools"]);
        Assert.DoesNotContain("\"enum\":[\"\"]", serialized);
        Assert.Contains("\"link\":{\"type\":\"string\"}", serialized);
    }

    [Fact]
    public void ConvertResponse_ChatApplyPatchProxy_RebuildsExecCommand()
    {
        var response = ProtocolConverter.ConvertResponse(
            new Dictionary<string, object?>
            {
                ["id"] = "chatcmpl_patch",
                ["model"] = "upstream",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty,
                            ["tool_calls"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["id"] = "call_patch",
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = "apply_patch_update_file",
                                        ["arguments"] = JsonSerializer.Serialize(new
                                        {
                                            path = "data.json",
                                            hunks = new[]
                                            {
                                                new
                                                {
                                                    lines = new object[]
                                                    {
                                                        new { op = "remove", text = "old" },
                                                        new { op = "add", text = "new" }
                                                    }
                                                }
                                            }
                                        })
                                    }
                                }
                            }
                        },
                        ["finish_reason"] = "tool_calls"
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "local");

        var output = Assert.IsType<List<object?>>(response["output"]);
        var item = Assert.IsType<Dictionary<string, object?>>(output[0]);
        Assert.Equal("function_call", item["type"]);
        Assert.Equal("exec_command", item["name"]);
        Assert.Equal("call_patch", item["call_id"]);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.IsType<string>(item["arguments"]));
        Assert.NotNull(arguments);
        Assert.Contains("apply_patch <<'OPENCODEX_PATCH'", arguments["cmd"]);
        Assert.Contains("*** Update File: data.json", arguments["cmd"]);
    }

    [Fact]
    public void ConvertResponse_ChatApplyPatchText_UsesExecCommand()
    {
        const string patch = """
                             *** Begin Patch
                             *** Add File: cli_patch_probe.txt
                             +OK
                             *** End Patch
                             """;

        var response = ProtocolConverter.ConvertResponse(
            new Dictionary<string, object?>
            {
                ["id"] = "chatcmpl_patch",
                ["model"] = "upstream",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty,
                            ["tool_calls"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["id"] = "call_patch",
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = "apply_patch",
                                        ["arguments"] = JsonSerializer.Serialize(new { patch })
                                    }
                                }
                            }
                        },
                        ["finish_reason"] = "tool_calls"
                    }
                }
            },
            ProtocolConverter.Responses,
            ProtocolConverter.Chat,
            "local");

        var output = Assert.IsType<List<object?>>(response["output"]);
        var item = Assert.IsType<Dictionary<string, object?>>(output[0]);
        Assert.Equal("function_call", item["type"]);
        Assert.Equal("exec_command", item["name"]);
        Assert.Equal("call_patch", item["call_id"]);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.IsType<string>(item["arguments"]));
        Assert.NotNull(arguments);
        Assert.Contains("*** Add File: cli_patch_probe.txt", arguments["cmd"]);
        Assert.Contains("+OK", arguments["cmd"]);
    }

    [Fact]
    public async Task ChatToResponsesEvents_ApplyPatchProxy_EmitsExecCommand()
    {
        var arguments = JsonSerializer.Serialize(new
        {
            path = "data.json",
            hunks = new[]
            {
                new
                {
                    lines = new object[]
                    {
                        new { op = "remove", text = "old" },
                        new { op = "add", text = "new" }
                    }
                }
            }
        });
        var firstChunk = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["id"] = "call_patch",
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = "apply_patch_update_file",
                                    ["arguments"] = arguments
                                }
                            }
                        }
                    },
                    ["finish_reason"] = null
                }
            }
        };
        var lines = ToAsyncLines(
        [
            $"data: {JsonSerializer.Serialize(firstChunk)}\n",
            "\n",
            "data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"upstream\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n",
            "\n",
            "data: [DONE]\n",
            "\n"
        ]);
        var converted = new ConvertedStreamResult();
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
            lines,
            "local",
            converted,
            CancellationToken.None))
        {
            events.Add(line);
        }

        var body = string.Concat(events);
        Assert.Contains("\"type\":\"function_call\"", body);
        Assert.Contains("\"name\":\"exec_command\"", body);
        Assert.Contains("apply_patch <<'OPENCODEX_PATCH'", body);
        Assert.DoesNotContain("response.function_call_arguments.delta", body);
        Assert.DoesNotContain("\"name\":\"apply_patch_update_file\"", body);
    }

    [Fact]
    public async Task WebSearchContinuation_RemovesRequiredToolChoiceBeforeFinalAnswer()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-web-search-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var db = OpenCodexDbContextFactory.Create(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.WebSearchSettings.Add(new WebSearchSettings
            {
                Id = 1,
                Enabled = true,
                KeyUsageLimit = 5,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            db.TavilyKeys.Add(new TavilyKey
            {
                Position = 0,
                Provider = "tavily",
                ApiKey = "test-key",
                Enabled = true,
                UsageCount = 0,
                UsageLimit = 5,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            await db.SaveChangesAsync();
        }

        var upstream = new RecordingUpstreamClient(
            ChatToolResponse("call_web", "web_search", "{\"query\":\"OpenAI\"}"),
            ChatTextResponse("final answer"));
        var simulator = new WebSearchSimulator(
            upstream,
            new SuccessfulWebSearchClient(),
            new FixedSettingsProvider(dbPath));
        var result = await simulator.RunAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = ProtocolConverter.Chat
            },
            new Dictionary<string, object?>
            {
                ["model"] = "upstream",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "search" }
                },
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = "web_search",
                            ["parameters"] = new Dictionary<string, object?>()
                        }
                    }
                },
                ["tool_choice"] = "required"
            },
            new Dictionary<string, object?>
            {
                ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } },
                ["max_tool_calls"] = 2
            },
            "public-model",
            120,
            CancellationToken.None);

        Assert.Equal(2, upstream.Requests.Count);
        Assert.False(upstream.Requests[1].ContainsKey("tool_choice"));
        var output = Assert.IsType<List<object?>>(result.ResponsePayload["output"]);
        Assert.Contains(output, item => item is Dictionary<string, object?> entry && (string?)entry["type"] == "message");
    }

    [Fact]
    public async Task WebSearchStream_ExecutesRepeatedWebSearchCallsBeforeFinalAnswer()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "opencodex-web-search-tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var db = OpenCodexDbContextFactory.Create(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.WebSearchSettings.Add(new WebSearchSettings
            {
                Id = 1,
                Enabled = true,
                KeyUsageLimit = 5,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            db.TavilyKeys.Add(new TavilyKey
            {
                Position = 0,
                Provider = "tavily",
                ApiKey = "test-key",
                Enabled = true,
                UsageCount = 0,
                UsageLimit = 5,
                CreatedAt = 1,
                UpdatedAt = 1
            });
            await db.SaveChangesAsync();
        }

        var upstream = new RecordingStreamUpstreamClient(
            ChatToolStreamResponse("call_web_1", "web_search", "{\"query\":\"OpenAI first\"}"),
            ChatToolStreamResponse("call_web_2", "web_search", "{\"query\":\"OpenAI second\"}"),
            ChatTextStreamResponse("final answer"));
        var simulator = new WebSearchSimulator(
            upstream,
            new SuccessfulWebSearchClient(),
            new FixedSettingsProvider(dbPath));
        var streamResult = new WebSearchStreamResult();
        var events = new List<string>();
        await foreach (var line in simulator.RunChatStreamAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = ProtocolConverter.Chat
            },
            new Dictionary<string, object?>
            {
                ["model"] = "upstream",
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "search" }
                },
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = "web_search",
                            ["parameters"] = new Dictionary<string, object?>()
                        }
                    }
                }
            },
            new Dictionary<string, object?>
            {
                ["tools"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "web_search" } },
                ["max_tool_calls"] = 5
            },
            "public-model",
            120,
            streamResult,
            CancellationToken.None))
        {
            events.Add(line);
        }

        Assert.Equal(3, upstream.Requests.Count);
        var body = string.Concat(events);
        Assert.Contains("OpenAI first", body, StringComparison.Ordinal);
        Assert.Contains("OpenAI second", body, StringComparison.Ordinal);
        Assert.Contains("final answer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"function_call\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"web_search\"", body, StringComparison.Ordinal);
        var addedSearchItemIds = WebSearchStreamItemIds(events, "response.output_item.added");
        var doneSearchItemIds = WebSearchStreamItemIds(events, "response.output_item.done");
        Assert.Equal(new[] { "call_web_1", "call_web_2" }, addedSearchItemIds);
        Assert.Equal(addedSearchItemIds, doneSearchItemIds);

        Assert.NotNull(streamResult.ResponsePayload);
        var responsePayload = streamResult.ResponsePayload!;
        var output = Assert.IsType<List<object?>>(responsePayload["output"]);
        Assert.Equal(2, output.Count(item =>
            item is Dictionary<string, object?> entry && (string?)entry["type"] == "web_search_call"));
        Assert.Contains(output, item =>
            item is Dictionary<string, object?> entry && (string?)entry["type"] == "message");
        Assert.DoesNotContain(output, item =>
            item is Dictionary<string, object?> entry && (string?)entry["type"] == "function_call");

        Assert.NotNull(streamResult.Details);
        var details = streamResult.Details!;
        var calls = Assert.IsType<List<object?>>(details["calls"]);
        Assert.Equal(2, calls.Count);
    }

    private async Task<string> LoginAndReadSessionCookie()
    {
        return await LoginAndReadSessionCookie(_client);
    }

    private static async Task<string> LoginAndReadSessionCookie(HttpClient client)
    {
        var response = await client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = OpenCodexApiFactory.AdminPassword
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(".AspNetCore.Session=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(cookie));
        return cookie;
    }

    private Task<HttpResponseMessage> SendJsonWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        return SendJsonWithCookie(_client, method, requestUri, cookie, body);
    }

    private static Task<HttpResponseMessage> SendJsonWithCookie(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string cookie,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendWithCookie(
        HttpMethod method,
        string requestUri,
        string cookie)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("Cookie", cookie);
        return _client.SendAsync(request);
    }

    private static async Task<string> ReadStringProperty(
        HttpResponseMessage response,
        params string[] path)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var current = document.RootElement;
        foreach (var key in path)
        {
            current = current.GetProperty(key);
        }

        return current.GetString() ?? string.Empty;
    }

    private static Dictionary<string, object?> ChatToolResponse(
        string callId,
        string name,
        string arguments)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = string.Empty,
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = callId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = name,
                                    ["arguments"] = arguments
                                }
                            }
                        }
                    },
                    ["finish_reason"] = "tool_calls"
                }
            }
        };
    }

    private static Dictionary<string, object?> ChatTextResponse(string text)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_text",
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    },
                    ["finish_reason"] = "stop"
                }
            }
        };
    }

    private static Dictionary<string, object?> ResponsesTextResponse(string text)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "resp_test",
            ["model"] = "upstream-model",
            ["output"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = text
                        }
                    }
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = 1,
                ["output_tokens"] = 1
            }
        };
    }

    private static Dictionary<string, object?> EmptyStringEnumParameters()
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["formatting"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["link"] = new Dictionary<string, object?>
                        {
                            ["anyOf"] = new List<object?>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new List<object?> { string.Empty }
                                },
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static async IAsyncEnumerable<string> ToAsyncLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private static IReadOnlyList<string> ChatToolStreamResponse(
        string callId,
        string name,
        string arguments)
    {
        var chunk = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["id"] = callId,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = name,
                                    ["arguments"] = arguments
                                }
                            }
                        }
                    },
                    ["finish_reason"] = null
                }
            }
        };
        var done = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_tool",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>(),
                    ["finish_reason"] = "tool_calls"
                }
            }
        };
        return ToSseLines(chunk, done);
    }

    private static IReadOnlyList<string> ChatTextStreamResponse(string text)
    {
        var chunk = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_text",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    },
                    ["finish_reason"] = null
                }
            }
        };
        var done = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_text",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "upstream",
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>(),
                    ["finish_reason"] = "stop"
                }
            }
        };
        return ToSseLines(chunk, done);
    }

    private static IReadOnlyList<string> ToSseLines(params Dictionary<string, object?>[] chunks)
    {
        var lines = new List<string>();
        foreach (var chunk in chunks)
        {
            lines.Add($"data: {JsonSerializer.Serialize(chunk)}\n");
            lines.Add("\n");
        }

        lines.Add("data: [DONE]\n");
        lines.Add("\n");
        return lines;
    }

    private static List<string> WebSearchStreamItemIds(
        IEnumerable<string> events,
        string eventName)
    {
        var ids = new List<string>();
        foreach (var line in events.Where(line =>
                     line.StartsWith($"event: {eventName}\n", StringComparison.Ordinal)))
        {
            var dataLine = line
                .Split('\n')
                .FirstOrDefault(part => part.StartsWith("data:", StringComparison.Ordinal));
            if (dataLine is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(dataLine["data:".Length..]);
            if (!document.RootElement.TryGetProperty("item", out var item)
                || !item.TryGetProperty("type", out var type)
                || type.GetString() != "web_search_call"
                || !item.TryGetProperty("id", out var id))
            {
                continue;
            }

            ids.Add(id.GetString() ?? string.Empty);
        }

        return ids;
    }

    private sealed class ProxyCompatibilityApiFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, object?>[] _responses;

        public ProxyCompatibilityApiFactory(params Dictionary<string, object?>[] responses)
        {
            _responses = responses;
            Upstream = new RecordingUpstreamClient(_responses);
        }

        public string DbPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "opencodex-proxy-compatibility-tests",
            $"{Guid.NewGuid():N}.db");

        public RecordingUpstreamClient Upstream { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OPENCODEX_ADMIN_USERNAME"] = "admin",
                    ["OPENCODEX_ADMIN_PASSWORD"] = OpenCodexApiFactory.AdminPassword,
                    ["OPENCODEX_DB_PATH"] = DbPath,
                    ["OPENCODEX_DEFAULT_TIMEOUT"] = "120"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUpstreamClient>();
                services.AddSingleton<IUpstreamClient>(Upstream);
            });
        }
    }

    private sealed class RecordingUpstreamClient : IUpstreamClient
    {
        private readonly Queue<Dictionary<string, object?>> _responses;

        public RecordingUpstreamClient(params Dictionary<string, object?>[] responses)
        {
            _responses = new Queue<Dictionary<string, object?>>(responses);
        }

        public List<Dictionary<string, object?>> Requests { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Requests.Add(WebSearchPayload.DeepCopyObject(payload));
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingStreamUpstreamClient : IUpstreamClient
    {
        private readonly Queue<IReadOnlyList<string>> _responses;

        public RecordingStreamUpstreamClient(params IReadOnlyList<string>[] responses)
        {
            _responses = new Queue<IReadOnlyList<string>>(responses);
        }

        public List<Dictionary<string, object?>> Requests { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("stream test upstream does not support non-stream requests");
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(WebSearchPayload.DeepCopyObject(payload));
            foreach (var line in _responses.Dequeue())
            {
                yield return line;
                await Task.Yield();
            }
        }
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticJsonHandler(string json)
        {
            _json = json;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SuccessfulWebSearchClient : IWebSearchClient
    {
        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSearchProviderResult(
                true,
                200,
                1,
                null,
                null,
                new WebSearchSummary(
                    "OpenAI result",
                    [new Dictionary<string, object?> { ["title"] = "OpenAI", ["url"] = "https://example.test" }],
                    null),
                new Dictionary<string, object?>()));
        }
    }

    private sealed class FixedSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly string _dbPath;

        public FixedSettingsProvider(string dbPath)
        {
            _dbPath = dbPath;
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return new OpenCodexRuntimeSettings(_dbPath, "admin", "password", 120);
        }
    }
}
