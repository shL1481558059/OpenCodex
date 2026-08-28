using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 覆盖图片识别转移模型的 per-owner 配置服务:保存校验、owner 收敛、可用性判定、
/// 候选列表与运行时快照。夹具使用临时 SQLite 库和真实 ModelCatalogService,
/// 让图片能力判定走真实的 ChannelModelInfo 解析。
/// </summary>
public sealed class VisionTransferSettingsServiceTests
{
    private static readonly Guid AdminUserId = Guid.Parse("88888888-8888-8888-8888-888888888801");
    private static readonly Guid AliceUserId = Guid.Parse("88888888-8888-8888-8888-888888888802");
    private static readonly Guid BobUserId = Guid.Parse("88888888-8888-8888-8888-888888888803");

    [Fact]
    public void Save_FirstCallInserts_SecondCallUpdatesSameRow()
    {
        var fixture = CreateFixture();

        var first = fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a"));

        Assert.True(first.Succeeded);
        Assert.True(first.Payload!.Configured);
        Assert.Equal("alice", first.Payload.OwnerUsername);
        Assert.Equal("upstream-vision-a", first.Payload.Primary!.UpstreamModel);
        Assert.True(first.Payload.Primary.Available);
        var createdRow = fixture.Context.VisionTransferSettings.Single();
        Assert.Equal(createdRow.CreatedAt, createdRow.UpdatedAt);

        var second = fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionSecondaryChannelId,
            "vision-b",
            fixture.Ids.AliceVisionChannelId,
            "vision-a"));

        Assert.True(second.Succeeded);
        var updatedRow = fixture.Context.VisionTransferSettings.Single();
        Assert.Equal(AliceUserId, updatedRow.OwnerUserId);
        Assert.Equal("vision-b", updatedRow.PrimaryModel);
        Assert.Equal("vision-a", updatedRow.FallbackModel);
        Assert.Equal(createdRow.Id, updatedRow.Id);
    }

    [Fact]
    public void Save_FallbackOmitted_Succeeds()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a"));

        Assert.True(result.Succeeded);
        Assert.Null(result.Payload!.Fallback);
        var row = fixture.Context.VisionTransferSettings.Single();
        Assert.Null(row.FallbackChannelId);
        Assert.Null(row.FallbackModel);
    }

    [Fact]
    public void Save_ModelNameIsTrimmedBeforeMatching()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "  vision-a  "));

        Assert.True(result.Succeeded);
        Assert.Equal("vision-a", fixture.Context.VisionTransferSettings.Single().PrimaryModel);
    }

    [Fact]
    public void Save_PrimaryHalfConfigured_Returns400()
    {
        var fixture = CreateFixture();

        var missingModel = fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, null));
        var missingChannel = fixture.Service.Save(Request("alice", null, "vision-a"));

        Assert.Equal(400, missingModel.Code);
        Assert.Equal(400, missingChannel.Code);
    }

    [Fact]
    public void Save_FallbackHalfConfigured_Returns400()
    {
        var fixture = CreateFixture();

        var onlyChannel = fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionChannelId,
            "vision-a",
            fixture.Ids.AliceVisionSecondaryChannelId));
        var onlyModel = fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionChannelId,
            "vision-a",
            fallbackModel: "vision-b"));

        Assert.Equal(400, onlyChannel.Code);
        Assert.Equal(400, onlyModel.Code);
    }

    [Fact]
    public void Save_ChannelNotFound_Returns400()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", Guid.NewGuid(), "vision-a"));

        Assert.Equal(400, result.Code);
        Assert.Contains("does not exist", result.Description);
    }

    [Fact]
    public void Save_ChannelOwnedByAnotherOwner_Returns400()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.BobVisionChannelId, "vision-d"));

        Assert.Equal(400, result.Code);
        Assert.Contains("does not belong", result.Description);
    }

    [Fact]
    public void Save_DisabledChannel_Returns400()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.AliceDisabledChannelId, "vision-c"));

        Assert.Equal(400, result.Code);
        Assert.Contains("disabled", result.Description);
    }

    [Fact]
    public void Save_ModelMappingMissing_Returns400()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "not-mapped"));

        Assert.Equal(400, result.Code);
        Assert.Contains("not found in channel", result.Description);
    }

    [Fact]
    public void Save_ModelWithoutImageCapability_Returns400WithCapabilityGuidance()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("alice", fixture.Ids.AliceTextChannelId, "text-a"));

        Assert.Equal(400, result.Code);
        Assert.Contains("image support", result.Description);
        Assert.Contains("supports_image", result.Description);
    }

    [Fact]
    public void Save_FallbackIdenticalToPrimary_Returns400()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionChannelId,
            "vision-a",
            fixture.Ids.AliceVisionChannelId,
            "vision-a"));

        Assert.Equal(400, result.Code);
        Assert.Contains("identical", result.Description);
    }

    [Fact]
    public void Save_ConcurrentInsertConflict_RetriesAsUpdate()
    {
        var fixture = CreateFixture();
        var request = Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a");

        var first = fixture.Service.Save(request);
        Assert.True(first.Succeeded);

        var second = fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionSecondaryChannelId,
            "vision-b",
            fixture.Ids.AliceVisionChannelId,
            "vision-a"));

        Assert.True(second.Succeeded);
        var row = fixture.Context.VisionTransferSettings.Single();
        Assert.Equal("vision-b", row.PrimaryModel);
        Assert.Equal("vision-a", row.FallbackModel);
    }

    [Fact]
    public void Save_OwnerNotFound_Returns404()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("ghost", fixture.Ids.AliceVisionChannelId, "vision-a"));

        Assert.Equal(404, result.Code);
        Assert.Contains("owner user not found", result.Description);
    }

    [Fact]
    public void NonSuperadmin_OwnerUsernameIsForcedToSelf()
    {
        var fixture = CreateFixture("alice", "user");

        // alice 谎报 owner_username=bob,并引用自己的渠道:应写进 alice 自己那行。
        var saved = fixture.Service.Save(Request("bob", fixture.Ids.AliceVisionChannelId, "vision-a"));

        Assert.True(saved.Succeeded);
        Assert.Equal("alice", saved.Payload!.OwnerUsername);
        var row = fixture.Context.VisionTransferSettings.Single();
        Assert.Equal(AliceUserId, row.OwnerUserId);

        // 读和候选同样被收敛回自己。
        Assert.Equal("alice", fixture.Service.Read("bob").Payload!.OwnerUsername);
        Assert.Equal("alice", fixture.Service.ListCandidates("bob").Payload!.OwnerUsername);

        // 删除也只影响自己那行。
        Assert.True(fixture.Service.Delete("bob").Succeeded);
        Assert.Empty(fixture.Context.VisionTransferSettings);
    }

    [Fact]
    public void NonSuperadmin_CannotReferenceAnotherOwnerChannel()
    {
        var fixture = CreateFixture("alice", "user");

        var result = fixture.Service.Save(Request(null, fixture.Ids.BobVisionChannelId, "vision-d"));

        Assert.Equal(400, result.Code);
        Assert.Contains("does not belong", result.Description);
    }

    [Fact]
    public void Superadmin_ConfiguresOnBehalfOfAnotherOwner()
    {
        var fixture = CreateFixture();

        var result = fixture.Service.Save(Request("bob", fixture.Ids.BobVisionChannelId, "vision-d"));

        Assert.True(result.Succeeded);
        Assert.Equal("bob", result.Payload!.OwnerUsername);
        Assert.Equal(BobUserId, fixture.Context.VisionTransferSettings.Single().OwnerUserId);
        Assert.False(fixture.Service.Read("alice").Payload!.Configured);
    }

    [Fact]
    public void Delete_AfterSave_ConfiguredBecomesFalse()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);
        Assert.True(fixture.Service.Read("alice").Payload!.Configured);

        Assert.True(fixture.Service.Delete("alice").Succeeded);

        var afterDelete = fixture.Service.Read("alice");
        Assert.False(afterDelete.Payload!.Configured);
        Assert.Null(afterDelete.Payload.Primary);
        Assert.Empty(fixture.Context.VisionTransferSettings);
    }

    [Fact]
    public void Delete_WithoutExistingRow_IsIdempotent()
    {
        var fixture = CreateFixture();

        Assert.True(fixture.Service.Delete("alice").Succeeded);
    }

    [Fact]
    public void Read_ChannelDisabledAfterSave_MarksUnavailableWithReason()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);

        var channel = fixture.Context.Channels.Single(item => item.Id == fixture.Ids.AliceVisionChannelId);
        channel.Enabled = false;
        fixture.Context.SaveChanges();

        var status = fixture.Service.Read("alice").Payload!.Primary!;

        Assert.False(status.Available);
        Assert.Equal("channel_disabled", status.Reason);
    }

    [Fact]
    public void Read_ModelMappingRemovedAfterSave_MarksUnavailableWithReason()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);

        var channel = fixture.Context.Channels.Single(item => item.Id == fixture.Ids.AliceVisionChannelId);
        channel.ModelsJson = JsonSerializer.Serialize(new List<object?> { Mapping("other", "upstream-other") });
        fixture.Context.SaveChanges();

        var status = fixture.Service.Read("alice").Payload!.Primary!;

        Assert.False(status.Available);
        Assert.Equal("model_mapping_missing", status.Reason);
    }

    [Fact]
    public void Read_ImageCapabilityRevokedAfterSave_MarksUnavailableWithReason()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);

        var capability = fixture.Context.ChannelModelInfos
            .Single(item => item.ChannelId == fixture.Ids.AliceVisionChannelId);
        capability.CapabilitiesJson = """{"supports_image":false}""";
        fixture.Context.SaveChanges();

        var status = fixture.Service.Read("alice").Payload!.Primary!;

        Assert.False(status.Available);
        Assert.Equal("image_capability_revoked", status.Reason);
    }

    [Fact]
    public void Read_ChannelDeletedAfterSave_MarksUnavailableWithReason()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);

        var channel = fixture.Context.Channels.Single(item => item.Id == fixture.Ids.AliceVisionChannelId);
        fixture.Context.Channels.Remove(channel);
        fixture.Context.SaveChanges();

        var status = fixture.Service.Read("alice").Payload!.Primary!;

        Assert.False(status.Available);
        Assert.Equal("channel_deleted", status.Reason);
    }

    [Fact]
    public void ListCandidates_ReturnsOnlyEnabledOwnedImageCapableModels()
    {
        var fixture = CreateFixture();

        var candidates = fixture.Service.ListCandidates("alice").Payload!.Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, item => item.ChannelId == fixture.Ids.AliceVisionChannelId && item.Model == "vision-a");
        Assert.Contains(candidates, item => item.ChannelId == fixture.Ids.AliceVisionSecondaryChannelId && item.Model == "vision-b");
        Assert.DoesNotContain(candidates, item => item.ChannelId == fixture.Ids.AliceTextChannelId);
        Assert.DoesNotContain(candidates, item => item.ChannelId == fixture.Ids.AliceDisabledChannelId);
        Assert.DoesNotContain(candidates, item => item.ChannelId == fixture.Ids.BobVisionChannelId);
        Assert.All(candidates, item => Assert.False(string.IsNullOrEmpty(item.UpstreamModel)));
    }

    [Fact]
    public void GetSnapshot_ReturnsConfiguredRoutes_AndNullWhenMissing()
    {
        var fixture = CreateFixture();

        Assert.Null(fixture.Service.GetSnapshot(AliceUserId));

        Assert.True(fixture.Service.Save(Request(
            "alice",
            fixture.Ids.AliceVisionChannelId,
            "vision-a",
            fixture.Ids.AliceVisionSecondaryChannelId,
            "vision-b")).Succeeded);

        var snapshot = fixture.Service.GetSnapshot(AliceUserId);

        Assert.NotNull(snapshot);
        Assert.Equal(fixture.Ids.AliceVisionChannelId, snapshot!.PrimaryChannelId);
        Assert.Equal("vision-a", snapshot.PrimaryModel);
        Assert.Equal(fixture.Ids.AliceVisionSecondaryChannelId, snapshot.FallbackChannelId);
        Assert.Equal("vision-b", snapshot.FallbackModel);
        Assert.Null(fixture.Service.GetSnapshot(BobUserId));
    }

    [Fact]
    public async Task DeletingUser_RemovesVisionTransferSettingsRow()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Service.Save(Request("alice", fixture.Ids.AliceVisionChannelId, "vision-a")).Succeeded);
        Assert.Single(fixture.Context.VisionTransferSettings);

        var users = new UserService(
            new StubSettingsProvider(),
            new StubWorkContext(AdminUserId, "admin", "superadmin"),
            new EfRepository<User>(fixture.Context),
            new EfRepository<AccessApiKey>(fixture.Context),
            new EfRepository<Channel>(fixture.Context),
            new EfRepository<VisionTransferSettings>(fixture.Context),
            new TestCacheService());

        var deleted = await users.DeleteUserAsync("alice");

        Assert.True(deleted.Succeeded);
        Assert.Empty(fixture.Context.VisionTransferSettings);
    }

    private static Fixture CreateFixture(
        string currentUsername = "admin",
        string currentRole = "superadmin")
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-vision-transfer-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var ids = new FixtureIds();
        using (var seed = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            seed.Database.Migrate();
            seed.Users.AddRange(
                UserEntity(AdminUserId, "admin", "superadmin"),
                UserEntity(AliceUserId, "alice", "user"),
                UserEntity(BobUserId, "bob", "user"));

            var aliceVision = ChannelEntity(AliceUserId, "alice-vision", 0, [Mapping("vision-a", "upstream-vision-a")]);
            var aliceVisionSecondary = ChannelEntity(AliceUserId, "alice-vision-2", 1, [Mapping("vision-b", "upstream-vision-b")]);
            var aliceText = ChannelEntity(AliceUserId, "alice-text", 2, [Mapping("text-a", "upstream-text-a")]);
            var aliceDisabled = ChannelEntity(AliceUserId, "alice-disabled", 3, [Mapping("vision-c", "upstream-vision-c")], enabled: false);
            var bobVision = ChannelEntity(BobUserId, "bob-vision", 0, [Mapping("vision-d", "upstream-vision-d")]);
            seed.Channels.AddRange(aliceVision, aliceVisionSecondary, aliceText, aliceDisabled, bobVision);

            // 只给这些 (渠道, 上游模型) 标注图片能力,text 渠道故意不标注。
            seed.ChannelModelInfos.AddRange(
                ImageCapableModel(aliceVision.Id, "upstream-vision-a"),
                ImageCapableModel(aliceVisionSecondary.Id, "upstream-vision-b"),
                ImageCapableModel(aliceDisabled.Id, "upstream-vision-c"),
                ImageCapableModel(bobVision.Id, "upstream-vision-d"));
            seed.SaveChanges();

            ids.AliceVisionChannelId = aliceVision.Id;
            ids.AliceVisionSecondaryChannelId = aliceVisionSecondary.Id;
            ids.AliceTextChannelId = aliceText.Id;
            ids.AliceDisabledChannelId = aliceDisabled.Id;
            ids.BobVisionChannelId = bobVision.Id;
        }

        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var workContext = new StubWorkContext(
            currentUsername == "admin" ? AdminUserId : currentUsername == "alice" ? AliceUserId : BobUserId,
            currentUsername,
            currentRole);
        var catalog = new ModelCatalogService(
            new EfRepository<ModelProvider>(context),
            new EfRepository<ModelInfo>(context),
            new EfRepository<ChannelModelInfo>(context),
            new EfRepository<ModelPricingPlan>(context),
            new EfRepository<ModelPricingRule>(context),
            new EfRepository<ChannelModelMapping>(context),
            new EfRepository<Channel>(context),
            workContext,
            new TestCacheService());
        var service = new VisionTransferSettingsService(
            workContext,
            new EfRepository<VisionTransferSettings>(context),
            new EfRepository<User>(context),
            new EfRepository<Channel>(context),
            catalog);
        return new Fixture(context, service, ids);
    }

    private static User UserEntity(Guid id, string username, string role)
    {
        return new User
        {
            Id = id,
            Username = username,
            PasswordHash = "hash",
            Role = role,
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
    }

    private static VisionTransferSettingsUpdateRequest Request(
        string? ownerUsername,
        Guid? primaryChannelId,
        string? primaryModel,
        Guid? fallbackChannelId = null,
        string? fallbackModel = null)
    {
        return new VisionTransferSettingsUpdateRequest
        {
            OwnerUsername = ownerUsername,
            Primary = new VisionTransferConfigItemDto
            {
                ChannelId = primaryChannelId,
                Model = primaryModel
            },
            Fallback = fallbackChannelId is null && fallbackModel is null
                ? null
                : new VisionTransferConfigItemDto
                {
                    ChannelId = fallbackChannelId,
                    Model = fallbackModel
                }
        };
    }

    private sealed class FixtureIds
    {
        public Guid AliceVisionChannelId { get; set; }

        public Guid AliceVisionSecondaryChannelId { get; set; }

        public Guid AliceTextChannelId { get; set; }

        public Guid AliceDisabledChannelId { get; set; }

        public Guid BobVisionChannelId { get; set; }
    }

    private sealed class Fixture
    {
        public Fixture(
            IOpenCodexDbContext context,
            VisionTransferSettingsService service,
            FixtureIds ids)
        {
            Context = context;
            Service = service;
            Ids = ids;
        }

        public IOpenCodexDbContext Context { get; }

        public VisionTransferSettingsService Service { get; }

        public FixtureIds Ids { get; }
    }

    private sealed class StubWorkContext : IWorkContext
    {
        private readonly SessionUser _user;

        public StubWorkContext(Guid userId, string username, string role)
        {
            _user = new SessionUser(userId, username, role, true);
        }

        public SessionUser? CurrentUser => _user;

        public bool IsSignedIn => true;

        public bool IsSuperadmin => _user.Role == "superadmin";

        public SessionUser RequireUser() => _user;

        public SessionUser RequireSuperadmin() => IsSuperadmin
            ? _user
            : throw new UnauthorizedAccessException("superadmin required");
    }

    /// <summary>
    /// 只为构造 UserService 提供最小运行时设置,`admin` 作为受保护用户名。
    /// </summary>
    private sealed class StubSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        public OpenCodexRuntimeSettings GetSettings()
        {
            return new OpenCodexRuntimeSettings("sqlite", "Data Source=:memory:", "admin", "password", 30);
        }
    }

    private static Channel ChannelEntity(
        Guid ownerUserId,
        string name,
        int position,
        IReadOnlyList<object?> models,
        bool enabled = true)
    {
        return new Channel
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Position = position,
            Priority = position,
            Capacity = 3,
            Name = name,
            Type = "chat",
            BaseUrl = "https://example.test/v1",
            ApiKey = "secret",
            AuthMode = "config",
            HeadersJson = "{}",
            TimeoutSeconds = 30,
            RetryCount = 0,
            CompatJson = "{}",
            ModelsJson = JsonSerializer.Serialize(models),
            Enabled = enabled,
            CreatedAt = 1,
            UpdatedAt = 1
        };
    }

    private static Dictionary<string, object?> Mapping(string model, string upstreamModel)
    {
        return new Dictionary<string, object?>
        {
            ["model"] = model,
            ["upstream_model"] = upstreamModel
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
}
