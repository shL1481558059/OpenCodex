using System.Text.Json;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyVisionRoutingTests
{
    [Fact]
    public void ValidateModelMappings_DefaultsSupportsImageToFalse()
    {
        var config = ConfigNormalizer.Normalize(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?>
            {
                ChannelConfig("chat", "admin", [ModelConfig("text-model", "text-upstream")])
            }
        });

        ConfigValidator.Validate(config);

        var channels = Assert.IsType<List<object?>>(config["channels"]);
        var channel = Assert.IsType<Dictionary<string, object?>>(channels[0]);
        var models = Assert.IsType<List<object?>>(channel["models"]);
        var mapping = Assert.IsType<Dictionary<string, object?>>(models[0]);
        Assert.False(Assert.IsType<bool>(mapping["supports_image"]));
    }

    [Fact]
    public void ValidateModelMappings_RejectsNonBooleanSupportsImage()
    {
        var config = ConfigNormalizer.Normalize(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?>
            {
                ChannelConfig(
                    "chat",
                    "admin",
                    [ModelConfig("text-model", "text-upstream", supportsImage: "true")])
            }
        });

        var exception = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(config));
        Assert.Contains("supports_image must be a boolean", exception.Message);
    }

    [Fact]
    public void ChooseRoute_ImageInput_KeepsOriginalTextModel()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "primary",
                0,
                [
                    ModelConfig("text-model", "text-upstream", false),
                    ModelConfig("same-vision", "same-vision-upstream", true)
                ]),
            ChannelEntity(
                "admin",
                "secondary",
                1,
                [ModelConfig("other-vision", "other-vision-upstream", true)]));

        var route = service.ChooseRoute("admin", "text-model", requestContainsImages: true);

        Assert.Equal("text-model", route.OriginalModel);
        Assert.Equal("text-upstream", route.UpstreamModel);
        Assert.Equal("primary", route.Channel["id"]);
        Assert.False(route.SupportsImage);
    }

    [Fact]
    public void ChooseRoute_ModelMappings_PrefersLowerPriority()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "later-position-better-priority",
                1,
                [ModelConfig("shared-model", "shared-upstream-b")],
                priority: 0),
            ChannelEntity(
                "admin",
                "earlier-position-worse-priority",
                0,
                [ModelConfig("shared-model", "shared-upstream-a")],
                priority: 3));

        var route = service.ChooseRoute("admin", "shared-model");

        Assert.Equal("later-position-better-priority", route.Channel["id"]);
        Assert.Equal("shared-upstream-b", route.UpstreamModel);
    }

    [Fact]
    public void ChooseRoute_ModelMappings_SamePriorityFallsBackToPosition()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "position-1",
                1,
                [ModelConfig("shared-model", "shared-upstream-b")],
                priority: 2),
            ChannelEntity(
                "admin",
                "position-0",
                0,
                [ModelConfig("shared-model", "shared-upstream-a")],
                priority: 2));

        var route = service.ChooseRoute("admin", "shared-model");

        Assert.Equal("position-0", route.Channel["id"]);
        Assert.Equal("shared-upstream-a", route.UpstreamModel);
    }

    [Fact]
    public void ChooseOcrRoute_ImageInput_UsesSameChannelVisionModelFirst()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "primary",
                0,
                [
                    ModelConfig("text-model", "text-upstream", false),
                    ModelConfig("same-vision", "same-vision-upstream", true)
                ]),
            ChannelEntity(
                "admin",
                "secondary",
                1,
                [ModelConfig("other-vision", "other-vision-upstream", true)]));

        var route = service.ChooseOcrRoute("admin", "text-model");

        Assert.NotNull(route);
        Assert.Equal("same-vision", route!.OriginalModel);
        Assert.Equal("same-vision-upstream", route.UpstreamModel);
        Assert.Equal("primary", route.Channel["id"]);
        Assert.True(route.SupportsImage);
    }

    [Fact]
    public void ChooseOcrRoute_ImageInput_FallsBackToLaterChannelVisionModel()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "primary",
                0,
                [ModelConfig("text-model", "text-upstream", false)]),
            ChannelEntity(
                "admin",
                "secondary",
                1,
                [ModelConfig("other-vision", "other-vision-upstream", true)]));

        var route = service.ChooseOcrRoute("admin", "text-model");

        Assert.NotNull(route);
        Assert.Equal("other-vision", route!.OriginalModel);
        Assert.Equal("other-vision-upstream", route.UpstreamModel);
        Assert.Equal("secondary", route.Channel["id"]);
        Assert.True(route.SupportsImage);
    }

    [Fact]
    public void ChooseRoute_ImageInput_KeepsOriginalVisionModel()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "primary",
                0,
                [
                    ModelConfig("vision-model", "vision-upstream", true),
                    ModelConfig("same-vision", "same-vision-upstream", true)
                ]));

        var route = service.ChooseRoute("admin", "vision-model", requestContainsImages: true);

        Assert.Equal("vision-model", route.OriginalModel);
        Assert.Equal("vision-upstream", route.UpstreamModel);
        Assert.Equal("primary", route.Channel["id"]);
    }

    [Fact]
    public void ChooseOcrRoute_ImageInput_ReturnsNullWhenNoVisionModelExists()
    {
        var service = CreateRouteService(
            ChannelEntity(
                "admin",
                "primary",
                0,
                [ModelConfig("text-model", "text-upstream", false)]));

        Assert.Null(service.ChooseOcrRoute("admin", "text-model"));
    }

    [Theory]
    [MemberData(nameof(ImagePayloads))]
    public void ContainsImageInput_DetectsImagePayloads(
        string protocol,
        Dictionary<string, object?> payload)
    {
        Assert.True(ProxyImageRequestDetector.ContainsImageInput(payload, protocol));
    }

    public static IEnumerable<object[]> ImagePayloads()
    {
        yield return
        [
            ProtocolConverter.Responses,
            new Dictionary<string, object?>
            {
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = "look" },
                            new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = "data:image/png;base64,AAAA" }
                        }
                    }
                }
            }
        ];

        yield return
        [
            ProtocolConverter.Chat,
            new Dictionary<string, object?>
            {
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "text", ["text"] = "look" },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object?> { ["url"] = "data:image/png;base64,AAAA" }
                            }
                        }
                    }
                }
            }
        ];

        yield return
        [
            ProtocolConverter.Messages,
            new Dictionary<string, object?>
            {
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["type"] = "text", ["text"] = "look" },
                            new Dictionary<string, object?>
                            {
                                ["type"] = "image",
                                ["source"] = new Dictionary<string, object?> { ["type"] = "base64", ["data"] = "AAAA" }
                            }
                        }
                    }
                }
            }
        ];
    }

    private static ProxyRouteService CreateRouteService(params Channel[] channels)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-vision-routing-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var context = OpenCodexDbContextFactory.Create(dbPath))
        {
            context.Database.EnsureCreated();
            context.Channels.AddRange(channels);
            context.SaveChanges();
        }

        return new ProxyRouteService(new FixedSettingsProvider(dbPath));
    }

    private static Channel ChannelEntity(
        string ownerUsername,
        string id,
        int position,
        IReadOnlyList<object?> models,
        int? priority = null,
        int? capacity = null)
    {
        return new Channel
        {
            OwnerUsername = ownerUsername,
            Id = id,
            Position = position,
            Priority = priority ?? position,
            Capacity = capacity,
            Name = id,
            Type = ProtocolConverter.Chat,
            BaseUrl = "https://example.test/v1",
            ApiKey = "secret",
            AuthMode = "config",
            HeadersJson = "{}",
            TimeoutSeconds = 30,
            RetryCount = 0,
            CompatJson = "{}",
            ModelsJson = JsonSerializer.Serialize(models),
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
    }

    private static Dictionary<string, object?> ChannelConfig(
        string id,
        string ownerUsername,
        IReadOnlyList<object?> models)
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = ownerUsername,
            ["id"] = id,
            ["name"] = id,
            ["type"] = ProtocolConverter.Chat,
            ["baseurl"] = "https://example.test/v1",
            ["apikey"] = "secret",
            ["auth_mode"] = "config",
            ["timeout_seconds"] = 30,
            ["retry_count"] = 0,
            ["models"] = models,
            ["enabled"] = true
        };
    }

    private static Dictionary<string, object?> ModelConfig(
        string model,
        string upstreamModel,
        object? supportsImage = null)
    {
        var mapping = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["upstream_model"] = upstreamModel
        };
        if (supportsImage is not null)
        {
            mapping["supports_image"] = supportsImage;
        }

        return mapping;
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
