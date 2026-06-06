using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Config;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Protocols;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminChannelDiagnosticsServiceTests
{
    [Fact]
    public async Task DiscoverModelsReturnsUniqueModelIdsFromUpstream()
    {
        var models = new FakeUpstreamModelClient
        {
            Response = new Dictionary<string, object?>
            {
                ["object"] = "list",
                ["data"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "gpt-4" },
                    new Dictionary<string, object?> { ["id"] = "gpt-4" },
                    new Dictionary<string, object?> { ["id"] = "gpt-4o" },
                    new Dictionary<string, object?> { ["object"] = "model" }
                }
            }
        };
        var service = Service(models: models);

        var result = await service.DiscoverModelsAsync(
            new Dictionary<string, object?>
            {
                ["channel"] = Channel()
            },
            CancellationToken.None);

        Assert.Equal(["gpt-4", "gpt-4o"], result.Models);
        Assert.Equal("list", result.Raw["object"]);
        var call = Assert.Single(models.Calls);
        Assert.Equal("chat", call.Channel["id"]);
        Assert.Equal("https://example.test/v1", call.Channel["baseurl"]);
        Assert.Equal(120, call.DefaultTimeout);
    }

    [Fact]
    public async Task TestChannelRewritesMappedModelAndAppliesCompat()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = ChatResponse("pong", "gpt-4")
        };
        var service = Service(upstream: upstream);

        var result = await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["channel"] = Channel(
                    models:
                    [
                        new Dictionary<string, object?>
                        {
                            ["model"] = "gpt-5",
                            ["upstream_model"] = "gpt-4"
                        }
                    ],
                    compat: new Dictionary<string, object?>
                    {
                        ["default_params"] = new Dictionary<string, object?> { ["temperature"] = 0.2 },
                        ["rename_params"] = new Dictionary<string, object?> { ["max_tokens"] = "max_completion_tokens" },
                        ["drop_params"] = new List<object?> { "temperature" },
                        ["force_params"] = new Dictionary<string, object?> { ["stream"] = false }
                    }),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["model"] = "gpt-5",
                    ["messages"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "ping"
                        }
                    },
                    ["max_tokens"] = 50
                }
            },
            CancellationToken.None);

        Assert.Equal("gpt-5", result.Model);
        Assert.Equal("gpt-4", result.UpstreamModel);
        Assert.Equal(["default:temperature", "rename:max_tokens->max_completion_tokens", "drop:temperature", "force:stream"], result.Compat);
        Assert.Equal("gpt-5", result.Response["model"]);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("gpt-4", call.Payload["model"]);
        Assert.Equal(50, call.Payload["max_completion_tokens"]);
        Assert.False(call.Payload.ContainsKey("max_tokens"));
        Assert.False(call.Payload.ContainsKey("temperature"));
        Assert.False((bool)call.Payload["stream"]!);
        Assert.Equal(120, call.DefaultTimeout);
    }

    [Fact]
    public async Task TestChannelBuildsFlatResponsesPayload()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = new Dictionary<string, object?>
            {
                ["id"] = "resp_1",
                ["model"] = "gpt-5",
                ["output"] = new List<object?>()
            }
        };
        var service = Service(upstream: upstream);

        await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "responses",
                ["type"] = ProtocolConverter.Responses,
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["model"] = "gpt-5",
                ["input"] = "hello",
                ["max_output_tokens"] = 99
            },
            CancellationToken.None);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("gpt-5", call.Payload["model"]);
        Assert.Equal("hello", call.Payload["input"]);
        Assert.Equal(99, call.Payload["max_output_tokens"]);
    }

    [Fact]
    public async Task TestChannelBuildsFlatChatPayloadWithDefaultInput()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = ChatResponse("pong", "gpt-5")
        };
        var service = Service(upstream: upstream);

        await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "chat",
                ["type"] = ProtocolConverter.Chat,
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["model"] = "gpt-5"
            },
            CancellationToken.None);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("gpt-5", call.Payload["model"]);
        Assert.Equal(256, call.Payload["max_tokens"]);
        var messages = AssertList(call.Payload["messages"]);
        var userMessage = Assert.Single(messages);
        Assert.Equal("user", userMessage["role"]);
        Assert.Equal("ping", userMessage["content"]);
    }

    [Fact]
    public async Task TestChannelBuildsFlatMessagesPayload()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = new Dictionary<string, object?>
            {
                ["id"] = "msg_1",
                ["model"] = "claude",
                ["content"] = new List<object?>()
            }
        };
        var service = Service(upstream: upstream);

        await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["id"] = "messages",
                ["type"] = ProtocolConverter.Messages,
                ["baseurl"] = "https://example.test/v1",
                ["apikey"] = "secret",
                ["model"] = "claude",
                ["input"] = "hello",
                ["max_output_tokens"] = "88"
            },
            CancellationToken.None);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("claude", call.Payload["model"]);
        Assert.Equal(88, call.Payload["max_tokens"]);
        var messages = AssertList(call.Payload["messages"]);
        var userMessage = Assert.Single(messages);
        Assert.Equal("user", userMessage["role"]);
        Assert.Equal("hello", userMessage["content"]);
    }

    [Fact]
    public async Task TestChannelRejectsUnsupportedCompatParameters()
    {
        var service = Service(upstream: new FakeUpstreamClient
        {
            Response = ChatResponse("pong", "gpt-5")
        });

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            service.TestChannelAsync(
                new Dictionary<string, object?>
                {
                    ["channel"] = Channel(
                        compat: new Dictionary<string, object?>
                        {
                            ["unsupported_params"] = new List<object?> { "temperature", "top_p" }
                        }),
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["model"] = "gpt-5",
                        ["messages"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["role"] = "user",
                                ["content"] = "ping"
                            }
                        },
                        ["top_p"] = 0.9,
                        ["temperature"] = 0.1
                    }
                },
                CancellationToken.None));

        Assert.Equal("upstream does not support parameter(s): temperature, top_p", exception.Message);
    }

    [Fact]
    public async Task TestChannelRenameCompatDoesNotOverwriteExistingTarget()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = ChatResponse("pong", "gpt-5")
        };
        var service = Service(upstream: upstream);

        var result = await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["channel"] = Channel(
                    compat: new Dictionary<string, object?>
                    {
                        ["rename_params"] = new Dictionary<string, object?> { ["max_tokens"] = "max_completion_tokens" }
                    }),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["model"] = "gpt-5",
                    ["messages"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "ping"
                        }
                    },
                    ["max_tokens"] = 50,
                    ["max_completion_tokens"] = 77
                }
            },
            CancellationToken.None);

        Assert.Equal(["rename:max_tokens->max_completion_tokens"], result.Compat);
        var call = Assert.Single(upstream.Calls);
        Assert.Equal(77, call.Payload["max_completion_tokens"]);
        Assert.False(call.Payload.ContainsKey("max_tokens"));
    }

    [Fact]
    public async Task TestChannelIgnoresUnsupportedCompatEntriesThatAreBlankOrAbsent()
    {
        var upstream = new FakeUpstreamClient
        {
            Response = ChatResponse("pong", "gpt-5")
        };
        var service = Service(upstream: upstream);

        var result = await service.TestChannelAsync(
            new Dictionary<string, object?>
            {
                ["channel"] = Channel(
                    compat: new Dictionary<string, object?>
                    {
                        ["unsupported_params"] = new List<object?> { string.Empty, "top_p", "missing" }
                    }),
                ["payload"] = new Dictionary<string, object?>
                {
                    ["model"] = "gpt-5",
                    ["messages"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = "ping"
                        }
                    },
                    ["temperature"] = 0.1
                }
            },
            CancellationToken.None);

        Assert.Empty(result.Compat);
        var call = Assert.Single(upstream.Calls);
        Assert.Equal(0.1, call.Payload["temperature"]);
    }

    [Fact]
    public async Task DiscoverModelsRejectsMissingChannel()
    {
        var service = Service();

        var exception = await Assert.ThrowsAsync<ConfigException>(() =>
            service.DiscoverModelsAsync(new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Equal("channel must be a JSON object", exception.Message);
    }

    private static AdminChannelDiagnosticsService Service(
        FakeUpstreamClient? upstream = null,
        FakeUpstreamModelClient? models = null)
    {
        return new AdminChannelDiagnosticsService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "pw", 120)),
            upstream ?? new FakeUpstreamClient(),
            models ?? new FakeUpstreamModelClient());
    }

    private static Dictionary<string, object?> Channel(
        IReadOnlyList<object?>? models = null,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        var channel = new Dictionary<string, object?>
        {
            ["id"] = "chat",
            ["type"] = ProtocolConverter.Chat,
            ["baseurl"] = "https://example.test/v1",
            ["apikey"] = "secret",
            ["auth_mode"] = "config",
            ["timeout_seconds"] = 30
        };
        if (models is not null)
        {
            channel["models"] = models;
        }

        if (compat is not null)
        {
            channel["compat"] = compat;
        }

        return channel;
    }

    private static Dictionary<string, object?> ChatResponse(string text, string model)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl_1",
            ["model"] = model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = text
                    }
                }
            }
        };
    }

    private static List<Dictionary<string, object?>> AssertList(object? value)
    {
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(value);
        return items
            .Select(item => Assert.IsAssignableFrom<Dictionary<string, object?>>(item))
            .ToList();
    }

    private sealed class FakeSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        private readonly OpenCodexRuntimeSettings _settings;

        public FakeSettingsProvider(OpenCodexRuntimeSettings settings)
        {
            _settings = settings;
        }

        public OpenCodexRuntimeSettings GetSettings()
        {
            return _settings;
        }
    }

    private sealed class FakeUpstreamClient : IUpstreamClient
    {
        public Dictionary<string, object?> Response { get; init; } = [];

        public List<UpstreamCall> Calls { get; } = [];

        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                payload.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));
            return Task.FromResult(Response);
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

    private sealed class FakeUpstreamModelClient : IUpstreamModelClient
    {
        public Dictionary<string, object?> Response { get; init; } = [];

        public List<UpstreamModelCall> Calls { get; } = [];

        public Task<Dictionary<string, object?>> ListModelsAsync(
            IReadOnlyDictionary<string, object?> channel,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new UpstreamModelCall(
                channel.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                defaultTimeout));
            return Task.FromResult(Response);
        }
    }

    private sealed record UpstreamCall(
        Dictionary<string, object?> Channel,
        Dictionary<string, object?> Payload,
        int DefaultTimeout);

    private sealed record UpstreamModelCall(
        Dictionary<string, object?> Channel,
        int DefaultTimeout);
}
