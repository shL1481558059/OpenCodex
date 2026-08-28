using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Config;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyVisionRoutingTests
{
    [Fact]
    public void NormalizeModelMappingsKeepsOnlyRequestAndUpstreamModel()
    {
        var config = ConfigNormalizer.Normalize(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?>
            {
                ChannelConfig("chat", "admin", [ModelConfig("text-model", "text-upstream", supportsImage: true)])
            }
        });

        var channels = Assert.IsType<List<object?>>(config["channels"]);
        var channel = Assert.IsType<Dictionary<string, object?>>(channels[0]);
        var models = Assert.IsType<List<object?>>(channel["models"]);
        var mapping = Assert.IsType<Dictionary<string, object?>>(models[0]);
        Assert.Equal(["model", "upstream_model"], mapping.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal("text-model", mapping["model"]);
        Assert.Equal("text-upstream", mapping["upstream_model"]);
    }

    [Fact]
    public void ValidateModelMappingsIgnoresLegacySupportsImageField()
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

        ConfigValidator.Validate(config);
    }

    [Fact]
    public void ValidateCompat_RejectsRemovedFallbackThinkingField()
    {
        var config = ConfigNormalizer.Normalize(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["owner_username"] = "admin",
                    ["id"] = "chat",
                    ["name"] = "chat",
                    ["type"] = ProtocolConverter.Chat,
                    ["baseurl"] = "https://example.test/v1",
                    ["apikey"] = "secret",
                    ["auth_mode"] = "config",
                    ["timeout_seconds"] = 30,
                    ["retry_count"] = 0,
                    ["capacity"] = 3,
                    ["models"] = new List<object?>
                    {
                        ModelConfig("text-model", "text-upstream")
                    },
                    ["compat"] = new Dictionary<string, object?>
                    {
                        ["fallback_thinking_on_tool_use"] = true
                    },
                    ["enabled"] = true
                }
            }
        });

        var exception = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(config));
        Assert.Contains("compat has unsupported field(s): fallback_thinking_on_tool_use", exception.Message);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    public void ValidateImagesChannel_AcceptsSupportedDialect(string dialect)
    {
        var channel = ChannelConfig("images", "admin", [ModelConfig("image-model", "image-upstream")]);
        channel["type"] = "images";
        channel["retry_count"] = 0;
        channel["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = dialect };

        ConfigValidator.Validate(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?> { channel }
        });
    }

    [Fact]
    public void ValidateImagesChannel_RequiresDialectAndZeroRetries()
    {
        var missingDialect = ChannelConfig("images", "admin", [ModelConfig("image-model", "image-upstream")]);
        missingDialect["type"] = "images";
        missingDialect["retry_count"] = 0;
        var dialectException = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(
            new Dictionary<string, object?> { ["channels"] = new List<object?> { missingDialect } }));
        Assert.Contains("compat.images_api_dialect is required", dialectException.Message);

        var retrying = ChannelConfig("images", "admin", [ModelConfig("image-model", "image-upstream")]);
        retrying["type"] = "images";
        retrying["retry_count"] = 1;
        retrying["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = "openai" };
        var retryException = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(
            new Dictionary<string, object?> { ["channels"] = new List<object?> { retrying } }));
        Assert.Contains("retry_count must be 0", retryException.Message);

        var invalidDialect = ChannelConfig("images-invalid", "admin", [ModelConfig("image-model", "image-upstream")]);
        invalidDialect["type"] = "images";
        invalidDialect["retry_count"] = 0;
        invalidDialect["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = "unknown" };
        var invalidDialectException = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(
            new Dictionary<string, object?> { ["channels"] = new List<object?> { invalidDialect } }));
        Assert.Contains("must be one of", invalidDialectException.Message);
    }

    [Fact]
    public void ValidateImagesDialect_RejectsNonImagesChannelAndMissingModelMapping()
    {
        var chat = ChannelConfig("chat", "admin", []);
        chat["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = "openai" };
        var chatException = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(
            new Dictionary<string, object?> { ["channels"] = new List<object?> { chat } }));
        Assert.Contains("only supported for images channels", chatException.Message);

        var images = ChannelConfig("images", "admin", []);
        images["type"] = "images";
        images["retry_count"] = 0;
        images["compat"] = new Dictionary<string, object?> { ["images_api_dialect"] = "openai" };
        var mappingException = Assert.Throws<ConfigException>(() => ConfigValidator.Validate(
            new Dictionary<string, object?> { ["channels"] = new List<object?> { images } }));
        Assert.Contains("at least one model mapping", mappingException.Message);
    }

    [Fact]
    public async Task ListRouteCandidates_AllowedTypesFilterRunsBeforeUnmappedFallback()
    {
        var service = CreateRouteService(
            ChannelEntity("admin", "chat-first", 0, [], type: ProtocolConverter.Chat),
            ChannelEntity("admin", "images-second", 1, [], type: "images"));

        var routes = await service.ListRouteCandidatesAsync(
            "admin",
            "image-model",
            allowedChannelTypes: new HashSet<string>(StringComparer.Ordinal) { "images" });

        var route = Assert.Single(routes);
        Assert.Equal("images-second", route.Channel["name"]);
    }

    [Fact]
    public async Task ChooseRoute_ImageInput_KeepsOriginalTextModel()
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

        var route = (await service.ListRouteCandidatesAsync("admin", "text-model"))[0];

        Assert.Equal("text-model", route.OriginalModel);
        Assert.Equal("text-upstream", route.UpstreamModel);
        Assert.Equal("primary", route.Channel["name"]);
        Assert.False(route.SupportsImage);
    }

    [Fact]
    public async Task ChooseRoute_ModelMappings_PrefersLowerPriority()
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

        var route = (await service.ListRouteCandidatesAsync("admin", "shared-model"))[0];

        Assert.Equal("later-position-better-priority", route.Channel["name"]);
        Assert.Equal("shared-upstream-b", route.UpstreamModel);
    }

    [Fact]
    public async Task ChooseRoute_ModelMappings_SamePriorityFallsBackToPosition()
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

        var route = (await service.ListRouteCandidatesAsync("admin", "shared-model"))[0];

        Assert.Equal("position-0", route.Channel["name"]);
        Assert.Equal("shared-upstream-a", route.UpstreamModel);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_ReturnsConfiguredPrimary_IgnoringOtherVisionModels()
    {
        var primary = ChannelEntity("admin", "primary", 0, [ModelConfig("text-model", "text-upstream")]);
        var configured = ChannelEntity("admin", "configured", 1, [ModelConfig("vision-a", "vision-a-upstream")]);
        var other = ChannelEntity("admin", "other", 2, [ModelConfig("vision-b", "vision-b-upstream")]);
        var service = CreateRouteService(
            VisionSettings(configured.Id, "vision-a"),
            [ImageCapableModel(configured.Id, "vision-a-upstream"), ImageCapableModel(other.Id, "vision-b-upstream")],
            primary,
            configured,
            other);

        var routes = await service.ListVisionTransferRoutesAsync("admin");

        Assert.True(routes.Configured);
        var route = Assert.Single(routes.Candidates);
        Assert.Equal("vision-a", route.OriginalModel);
        Assert.Equal("vision-a-upstream", route.UpstreamModel);
        Assert.Equal("configured", route.Channel["name"]);
        Assert.True(route.SupportsImage);
        Assert.True(route.MatchedModelMapping);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_ReturnsPrimaryThenFallback()
    {
        var primary = ChannelEntity("admin", "primary", 0, [ModelConfig("vision-a", "vision-a-upstream")]);
        var fallback = ChannelEntity("admin", "fallback", 1, [ModelConfig("vision-b", "vision-b-upstream")]);
        var service = CreateRouteService(
            VisionSettings(primary.Id, "vision-a", fallback.Id, "vision-b"),
            [ImageCapableModel(primary.Id, "vision-a-upstream"), ImageCapableModel(fallback.Id, "vision-b-upstream")],
            primary,
            fallback);

        var routes = await service.ListVisionTransferRoutesAsync("admin");

        Assert.Equal(2, routes.Candidates.Count);
        Assert.Equal("vision-a", routes.Candidates[0].OriginalModel);
        Assert.Equal("vision-b", routes.Candidates[1].OriginalModel);
        Assert.Empty(routes.UnavailableReason);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_PrimaryChannelDisabled_UsesFallbackOnly()
    {
        var primary = ChannelEntity("admin", "primary", 0, [ModelConfig("vision-a", "vision-a-upstream")]);
        primary.Enabled = false;
        var fallback = ChannelEntity("admin", "fallback", 1, [ModelConfig("vision-b", "vision-b-upstream")]);
        var service = CreateRouteService(
            VisionSettings(primary.Id, "vision-a", fallback.Id, "vision-b"),
            [ImageCapableModel(primary.Id, "vision-a-upstream"), ImageCapableModel(fallback.Id, "vision-b-upstream")],
            primary,
            fallback);

        var routes = await service.ListVisionTransferRoutesAsync("admin");

        var route = Assert.Single(routes.Candidates);
        Assert.Equal("vision-b", route.OriginalModel);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_ImageCapabilityRevoked_ReportsReason()
    {
        var configured = ChannelEntity("admin", "configured", 0, [ModelConfig("vision-a", "vision-a-upstream")]);
        var service = CreateRouteService(
            VisionSettings(configured.Id, "vision-a"),
            [],
            configured);

        var routes = await service.ListVisionTransferRoutesAsync("admin");

        Assert.True(routes.Configured);
        Assert.Empty(routes.Candidates);
        Assert.Equal(VisionTransferUnavailableReasons.ImageCapabilityRevoked, routes.UnavailableReason);
    }

    [Fact]
    public async Task ChooseRoute_ImageInput_KeepsOriginalVisionModel()
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

        var route = (await service.ListRouteCandidatesAsync("admin", "vision-model"))[0];

        Assert.Equal("vision-model", route.OriginalModel);
        Assert.Equal("vision-upstream", route.UpstreamModel);
        Assert.Equal("primary", route.Channel["name"]);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_WithoutConfiguration_ReportsNotConfigured_AndNeverAutoDiscovers()
    {
        // 渠道里明明有一个已标注图片能力的模型,但没有配置行时不允许被自动选中。
        var vision = ChannelEntity("admin", "vision", 0, [ModelConfig("vision-a", "vision-a-upstream")]);
        var service = CreateRouteService(
            null,
            [ImageCapableModel(vision.Id, "vision-a-upstream")],
            vision);

        var routes = await service.ListVisionTransferRoutesAsync("admin");

        Assert.False(routes.Configured);
        Assert.Empty(routes.Candidates);
        Assert.Equal(VisionTransferUnavailableReasons.NotConfigured, routes.UnavailableReason);
    }

    [Fact]
    public async Task ListVisionTransferRoutes_UnknownOwner_ReportsNotConfigured()
    {
        var service = CreateRouteService(
            ChannelEntity("admin", "primary", 0, [ModelConfig("text-model", "text-upstream")]));

        var routes = await service.ListVisionTransferRoutesAsync("ghost");

        Assert.False(routes.Configured);
        Assert.Empty(routes.Candidates);
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
        return CreateRouteService(null, [], channels);
    }

    private static ProxyRouteService CreateRouteService(
        VisionTransferSettings? visionSettings,
        IReadOnlyList<ChannelModelInfo> imageCapableModels,
        params Channel[] channels)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-vision-routing-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
context.Channels.AddRange(channels);
            if (imageCapableModels.Count > 0)
            {
                context.ChannelModelInfos.AddRange(imageCapableModels);
            }

            if (visionSettings is not null)
            {
                context.VisionTransferSettings.Add(visionSettings);
            }

            context.SaveChanges();
        }

        using (var seedContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            seedContext.Database.Migrate();
            if (!seedContext.Users.Any(u => u.Username == "admin"))
            {
                seedContext.Users.Add(new OpenCodex.Core.Domain.User
                {
                    Id = AdminUserId,
                    Username = "admin",
                    PasswordHash = "hash",
                    Role = "superadmin",
                    Enabled = true,
                    CreatedAt = 1,
                    UpdatedAt = 1
                });
                seedContext.SaveChanges();
            }
        }

        var routeContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var workContext = new TestWorkContext(AdminUserId, "admin", "superadmin");
        var catalog = new ModelCatalogService(
            new EfRepository<ModelProvider>(routeContext),
            new EfRepository<ModelInfo>(routeContext),
            new EfRepository<ChannelModelInfo>(routeContext),
            new EfRepository<ModelPricingPlan>(routeContext),
            new EfRepository<ModelPricingRule>(routeContext),
            new EfRepository<ChannelModelMapping>(routeContext),
            new EfRepository<Channel>(routeContext),
            workContext,
            new TestCacheService());
        return new ProxyRouteService(
            new EfRepository<OpenCodex.Core.Domain.Channel>(routeContext),
            new EfRepository<OpenCodex.Core.Domain.User>(routeContext),
            catalog,
            new TestCacheService(),
            new VisionTransferSettingsService(
                workContext,
                new EfRepository<VisionTransferSettings>(routeContext),
                new EfRepository<OpenCodex.Core.Domain.User>(routeContext),
                new EfRepository<OpenCodex.Core.Domain.Channel>(routeContext),
                catalog));
    }

    private static readonly Guid AdminUserId = Guid.Parse("77777777-7777-7777-7777-777777777701");

    private static VisionTransferSettings VisionSettings(
        Guid primaryChannelId,
        string primaryModel,
        Guid? fallbackChannelId = null,
        string? fallbackModel = null)
    {
        return new VisionTransferSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = AdminUserId,
            PrimaryChannelId = primaryChannelId,
            PrimaryModel = primaryModel,
            FallbackChannelId = fallbackChannelId,
            FallbackModel = fallbackModel,
            CreatedAt = 1,
            UpdatedAt = 1
        };
    }

    private static ChannelModelInfo ImageCapableModel(Guid channelId, string upstreamModel)
    {
        return new ChannelModelInfo
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            UpstreamModel = upstreamModel,
            ProviderId = Guid.NewGuid(),
            ModelKey = upstreamModel,
            DisplayName = upstreamModel,
            MatchPattern = upstreamModel,
            CapabilitiesJson = """{"supports_image":true}""",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
    }

    private static Channel ChannelEntity(
        string ownerUsername,
        string id,
        int position,
        IReadOnlyList<object?> models,
        int? priority = null,
        int? capacity = null,
        string? type = null)
    {
        return new Channel
        {
            OwnerUserId = AdminUserId,
            Id = Guid.NewGuid(),
            Position = position,
            Priority = priority ?? position,
            Capacity = capacity ?? 3,
            Name = id,
            Type = type ?? ProtocolConverter.Chat,
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
            ["capacity"] = 3,
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
            return new OpenCodexRuntimeSettings("sqlite", $"Data Source={_dbPath}", "admin", "password", 120);
        }
    }

    private sealed class TestWorkContext : IWorkContext
    {
        private readonly SessionUser _user;

        public TestWorkContext(Guid userId, string username, string role)
        {
            _user = new SessionUser(userId, username, role, true);
        }

        public SessionUser? CurrentUser => _user;

        public bool IsSignedIn => true;

        public bool IsSuperadmin => _user.Role == "superadmin";

        public SessionUser RequireUser()
        {
            return _user;
        }

        public SessionUser RequireSuperadmin()
        {
            return IsSuperadmin
                ? _user
                : throw new UnauthorizedAccessException("superadmin required");
        }
    }
}
