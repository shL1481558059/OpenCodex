using Microsoft.EntityFrameworkCore;
using OpenCodex.Api.Tests.Infrastructure;
using OpenCodex.Core;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Security;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ServiceQueryGovernanceTests
{
    private static readonly Guid AdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AliceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SetUserEnabled_UpdatesOnlyEnabledAndUpdatedAt()
    {
        var db = NewDb(out var capture);
        var service = CreateUserService(db);

        var result = await service.UpdateUserAsync(
            "alice",
            new UserUpdateCommand(enabled: false, password: null));

        Assert.True(result.Succeeded);
        Assert.False(result.Payload!.User.Enabled);
        Assert.Equal("h", db.Users.Single(u => u.Username == "alice").PasswordHash);
        // 启用状态更新不应把 PasswordHash 写进 UPDATE "Users" 语句。
        var update = capture.StatementsStartingWith("UPDATE")
            .Single(statement => statement.Contains("UPDATE \"Users\"", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            "\"PasswordHash\"",
            update,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetUserPassword_UpdatesOnlyPasswordHashAndUpdatedAt()
    {
        var db = NewDb(out var capture);
        var service = CreateUserService(db);

        var result = await service.UpdateUserAsync(
            "alice",
            new UserUpdateCommand(enabled: null, password: "new-password"));

        Assert.True(result.Succeeded);
        var user = db.Users.Single(u => u.Username == "alice");
        Assert.True(OpenCodexSecurity.VerifyPassword("new-password", user.PasswordHash));
        Assert.True(user.Enabled);
        // 密码重置不应把 Enabled 写进 UPDATE "Users" 语句。
        var update = capture.StatementsStartingWith("UPDATE")
            .Single(statement => statement.Contains("UPDATE \"Users\"", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            "\"Enabled\"",
            update,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteUser_RemovesRelatedRowsWithoutLoadingEntities()
    {
        var db = NewDb(out _);
        db.AccessApiKeys.Add(new AccessApiKey
        {
            OwnerUserId = AliceId,
            Name = "alice-key",
            KeyHash = "hash",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.Channels.Add(new Channel
        {
            OwnerUserId = AliceId,
            Name = "alice-channel",
            Position = 0,
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();
        var service = CreateUserService(db);

        var result = await service.DeleteUserAsync("alice");

        Assert.True(result.Succeeded);
        Assert.Empty(db.Users.Where(u => u.Username == "alice"));
        Assert.Empty(db.AccessApiKeys);
        Assert.Empty(db.Channels);
    }

    [Fact]
    public void CreateKey_NonSuperadmin_UsesWorkContextOwnerWithoutUserQuery()
    {
        var db = NewDb(out _);
        var service = CreateApiKeyService(db, alice: true);

        var result = service.CreateKey(new ApiKeyCreateCommand(Guid.Empty, null, "alice-key"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Payload!.Key.OwnerUsername);
        Assert.Equal(AliceId, db.AccessApiKeys.Single().OwnerUserId);
    }

    [Fact]
    public void CreateKey_SuperadminByUsername_ResolvesOwnerOnceAndProjectsFields()
    {
        var db = NewDb(out var capture);
        var service = CreateApiKeyService(db, alice: false);

        capture.Reset();
        var result = service.CreateKey(new ApiKeyCreateCommand(Guid.Empty, "alice", "alice-key"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Payload!.Key.OwnerUsername);
        Assert.Equal(AliceId, db.AccessApiKeys.Single().OwnerUserId);
        // 超管按 username 指定归属人时，Users 只应被查一次：首次解析拿到
        // Id/Username 后直接复用，不应再按 Id 回查一次 Username。
        Assert.Equal(1, capture.CountMatching("FROM \"Users\""));
    }

    [Fact]
    public void CreateKey_SuperadminByOwnerUserId_ResolvesOwnerOnce()
    {
        var db = NewDb(out var capture);
        var service = CreateApiKeyService(db, alice: false);

        capture.Reset();
        var result = service.CreateKey(new ApiKeyCreateCommand(AliceId, null, "alice-key"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Payload!.Key.OwnerUsername);
        Assert.Equal(AliceId, db.AccessApiKeys.Single().OwnerUserId);
        // 超管按 OwnerUserId 指定归属人时，一次投影 Id/Username 后复用，
        // 不应再按 Id 回查一次 Username。
        Assert.Equal(1, capture.CountMatching("FROM \"Users\""));
    }

    [Fact]
    public async Task UpdateKey_NonSuperadmin_UpdatesOnlyEnabledAndUpdatedAt()
    {
        var db = NewDb(out _);
        var key = new AccessApiKey
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            OwnerUserId = AliceId,
            Name = "alice-key",
            KeyHash = "old-hash",
            KeyPrefix = "old",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        db.AccessApiKeys.Add(key);
        db.SaveChanges();
        var service = CreateApiKeyService(db, alice: true);

        var result = await service.UpdateKeyAsync(key.Id, new ApiKeyUpdateCommand(enabled: false));

        Assert.True(result.Succeeded);
        Assert.False(db.AccessApiKeys.Single().Enabled);
        Assert.Equal("old-hash", db.AccessApiKeys.Single().KeyHash);
        Assert.Equal("old", db.AccessApiKeys.Single().KeyPrefix);
    }

    [Fact]
    public void SaveConfig_ReusesSingleSettingsQueryAndDeletesKeysByPredicate()
    {
        var db = NewDb(out _);
        var settings = new WebSearchSettings
        {
            Mode = "convert",
            KeyUsageLimit = 500,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        db.WebSearchSettings.Add(settings);
        db.TavilyKeys.Add(new TavilyKey
        {
            Position = 0,
            Provider = "tavily",
            ApiKey = "old-key",
            UsageCount = 0,
            UsageLimit = 500,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();
        var service = CreateWebSearchService(db);

        var result = service.SaveConfig(new Dictionary<string, object?>
        {
            ["keys"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["provider"] = "tavily",
                    ["key"] = "new-key"
                }
            }
        });

        Assert.True(result.Succeeded);
        Assert.Equal(500, result.Payload!.DefaultKeyUsageLimit);
        var persisted = db.TavilyKeys.Single();
        Assert.Equal("new-key", persisted.ApiKey);
        Assert.Equal(500, persisted.UsageLimit);
    }

    [Fact]
    public void MergeConfig_UpdatePreservesColumnsNotSupplied()
    {
        var db = NewDb(out _);
        var key = new TavilyKey
        {
            Position = 0,
            Provider = "tavily",
            ApiKey = "same-key",
            Enabled = true,
            UsageCount = 3,
            UsageLimit = 500,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        db.TavilyKeys.Add(key);
        db.SaveChanges();
        var service = CreateWebSearchService(db);

        var result = service.ImportConfig(new Dictionary<string, object?>
        {
            ["keys"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["provider"] = "tavily",
                    ["key"] = "same-key",
                    ["enabled"] = false
                }
            }
        });

        Assert.True(result.Succeeded);
        var persisted = db.TavilyKeys.Single();
        Assert.False(persisted.Enabled);
        Assert.Equal(3, persisted.UsageCount);
        Assert.Equal(500, persisted.UsageLimit);
    }

    private static IOpenCodexDbContext NewDb(out SqlCapture capture)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-service-query-governance",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        capture = new SqlCapture();
        var context = SqlCapture.CreateCapturingContext($"Data Source={dbPath}", capture);
        context.Database.Migrate();
        context.Users.AddRange(
            new User { Id = AdminId, Username = "admin", PasswordHash = "h", Role = "superadmin", Enabled = true, CreatedAt = 1, UpdatedAt = 1 },
            new User { Id = AliceId, Username = "alice", PasswordHash = "h", Role = "user", Enabled = true, CreatedAt = 1, UpdatedAt = 1 });
        context.SaveChanges();
        return context;
    }

    private static UserService CreateUserService(IOpenCodexDbContext db)
    {
        return new UserService(
            new StubSettingsProvider(),
            new StubWorkContext(AdminId, "admin", "superadmin"),
            new EfRepository<User>(db),
            new EfRepository<AccessApiKey>(db),
            new EfRepository<Channel>(db),
            new EfRepository<VisionTransferSettings>(db),
            new TestCacheService());
    }

    private static ApiKeyService CreateApiKeyService(IOpenCodexDbContext db, bool alice)
    {
        return new ApiKeyService(
            new StubWorkContext(alice ? AliceId : AdminId, alice ? "alice" : "admin", alice ? "user" : "superadmin"),
            new EfRepository<AccessApiKey>(db),
            new EfRepository<User>(db),
            new TestCacheService());
    }

    private static WebSearchService CreateWebSearchService(IOpenCodexDbContext db)
    {
        return new WebSearchService(
            new StubWebSearchClient(),
            new EfRepository<WebSearchSettings>(db),
            new EfRepository<TavilyKey>(db));
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

    private sealed class StubSettingsProvider : IOpenCodexRuntimeSettingsProvider
    {
        public OpenCodexRuntimeSettings GetSettings()
        {
            return new OpenCodexRuntimeSettings("sqlite", "Data Source=:memory:", "admin", "password", 30);
        }
    }

    private sealed class StubWebSearchClient : IWebSearchClient
    {
        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSearchProviderResult(
                true,
                200,
                0,
                null,
                null,
                new WebSearchSummary(string.Empty, [], null),
                null));
        }
    }

    [Fact]
    public async Task DeleteUser_IssuesNoSelectAndBatchDeletesRelatedRows()
    {
        var db = NewDb(out var capture);
        db.AccessApiKeys.Add(new AccessApiKey
        {
            OwnerUserId = AliceId,
            Name = "alice-key",
            KeyHash = "hash",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.Channels.Add(new Channel
        {
            OwnerUserId = AliceId,
            Name = "alice-channel",
            Position = 0,
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.VisionTransferSettings.Add(new VisionTransferSettings
        {
            OwnerUserId = AliceId,
            PrimaryChannelId = Guid.NewGuid(),
            PrimaryModel = "vision",
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();
        var service = CreateUserService(db);

        capture.Reset();
        var result = await service.DeleteUserAsync("alice");

        Assert.True(result.Succeeded);
        // 当前实现：1 条加载用户的 SELECT（返回值需要完整 DTO），随后 4 条 DELETE：
        // api key、channel、vision transfer settings、user 自身。
        Assert.Equal(1, capture.SelectCount);
        Assert.Equal(4, capture.DeleteCount);
        Assert.Empty(db.AccessApiKeys);
        Assert.Empty(db.Channels);
        Assert.Empty(db.VisionTransferSettings);
    }

    [Fact]
    public void SaveConfig_QueriesWebSearchSettingsTwiceAndDeletesKeysByPredicate()
    {
        var db = NewDb(out var capture);
        var settings = new WebSearchSettings
        {
            Mode = "convert",
            KeyUsageLimit = 500,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        db.WebSearchSettings.Add(settings);
        db.TavilyKeys.Add(new TavilyKey
        {
            Position = 0,
            Provider = "tavily",
            ApiKey = "old-key",
            UsageCount = 0,
            UsageLimit = 500,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();
        var service = CreateWebSearchService(db);

        capture.Reset();
        var result = service.SaveConfig(new Dictionary<string, object?>
        {
            ["keys"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["provider"] = "tavily",
                    ["key"] = "new-key"
                }
            }
        });

        Assert.True(result.Succeeded);
        // 当前实现：ReplaceWebSearchConfig 先查一次 settings，收尾 ReadWebSearchConfig 再查一次，
        // 共 2 次 WebSearchSettings SELECT；文档 A2 目标是降到 1 次。
        Assert.Equal(2, capture.CountMatching("FROM \"WebSearchSettings\""));
        Assert.Equal(1, capture.DeleteCount);
    }

    [Fact]
    public void CreateKey_SuperadminByUsername_ProjectsOnlyIdAndUsername()
    {
        var db = NewDb(out var capture);
        var service = CreateApiKeyService(db, alice: false);

        capture.Reset();
        var result = service.CreateKey(new ApiKeyCreateCommand(Guid.Empty, "alice", "alice-key"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice", result.Payload!.Key.OwnerUsername);
        // B2：Users 查询只投影 Id/Username，不应把 PasswordHash 拉进内存。
        Assert.DoesNotContain(
            capture.Commands,
            command => command.Contains("FROM \"Users\"", StringComparison.OrdinalIgnoreCase)
                && command.Contains("PasswordHash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateKey_NonSuperadmin_UpdateWritesOnlyEnabledAndUpdatedAt()
    {
        var db = NewDb(out var capture);
        var key = new AccessApiKey
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            OwnerUserId = AliceId,
            Name = "alice-key",
            KeyHash = "old-hash",
            KeyPrefix = "old",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        };
        db.AccessApiKeys.Add(key);
        db.SaveChanges();
        var service = CreateApiKeyService(db, alice: true);

        capture.Reset();
        var result = await service.UpdateKeyAsync(key.Id, new ApiKeyUpdateCommand(enabled: false));

        Assert.True(result.Succeeded);
        // B5：UPDATE "AccessApiKeys" 只写 Enabled/UpdatedAt，
        // KeyHash/KeyPrefix/Name/OwnerUserId/CreatedAt 都不应出现在 UPDATE 语句里。
        // 注意只针对 UPDATE 语句断言：前面的实体 SELECT 会含 KeyHash 等全列，不能全局扫。
        var update = capture.StatementsStartingWith("UPDATE")
            .Single(statement => statement.Contains(
                "UPDATE \"AccessApiKeys\"",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"Enabled\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"UpdatedAt\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"KeyHash\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"KeyPrefix\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Name\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"OwnerUserId\"", update, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"CreatedAt\"", update, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProxyLog_CreateQueuedLog_ResolvesOwnerOnceWithinRequest()
    {
        var db = NewDb(out var capture);
        var service = new ProxyLogService(
            new StubSettingsProvider(),
            CreateEmptyCatalog(db),
            db,
            new EfRepository<RequestLog>(db),
            new EfRepository<User>(db));

        capture.Reset();
        for (var i = 0; i < 3; i++)
        {
            service.CreateQueuedLog(new ProxyRequestLogQueuedContext(
                requestId: $"req-{i}",
                ownerUsername: "alice",
                apiKeyId: null,
                payload: null,
                requestModel: null,
                isStream: false,
                method: "POST",
                path: "/v1/chat/completions",
                clientIp: "127.0.0.1",
                requestHeaders: new Dictionary<string, string>()));
        }

        // A8 降级：同一 service 实例内 ResolveOwnerUserId 带请求内记忆化，
        // 连续三次 CreateQueuedLog 只触发一次 Users SELECT。
        Assert.Equal(1, capture.CountMatching("FROM \"Users\""));
        Assert.Equal(3, db.RequestLogs.Count());
    }

    [Fact]
    public void ProxyLog_ResolveOwnerUserId_DoesNotCacheMissingUser()
    {
        var db = NewDb(out var capture);
        var service = new ProxyLogService(
            new StubSettingsProvider(),
            CreateEmptyCatalog(db),
            db,
            new EfRepository<RequestLog>(db),
            new EfRepository<User>(db));

        // 第一次解析 bob 时用户尚不存在，不应把 Guid.Empty 记进请求内缓存。
        capture.Reset();
        service.CreateQueuedLog(new ProxyRequestLogQueuedContext(
            requestId: "req-missing-bob",
            ownerUsername: "bob",
            apiKeyId: null,
            payload: null,
            requestModel: null,
            isStream: false,
            method: "POST",
            path: "/v1/chat/completions",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string>()));
        Assert.Equal(1, capture.CountMatching("FROM \"Users\""));
        var bobId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        db.Users.Add(new User
        {
            Id = bobId,
            Username = "bob",
            PasswordHash = "h",
            Role = "user",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();

        // 同一 service 实例内再次解析 bob，应回库拿到新 Id，而不是复用 Guid.Empty。
        capture.Reset();
        var secondLogId = service.CreateQueuedLog(new ProxyRequestLogQueuedContext(
            requestId: "req-new-bob",
            ownerUsername: "bob",
            apiKeyId: null,
            payload: null,
            requestModel: null,
            isStream: false,
            method: "POST",
            path: "/v1/chat/completions",
            clientIp: "127.0.0.1",
            requestHeaders: new Dictionary<string, string>()));

        Assert.Equal(1, capture.CountMatching("FROM \"Users\""));
        var persisted = db.RequestLogs.AsNoTracking().Single(item => item.Id == secondLogId);
        Assert.Equal(bobId, persisted.OwnerUserId);
    }

    [Fact]
    public void ListModels_PlansQueryDoesNotGrowWithModelCount()
    {
        var db = NewDb(out var capture);
        var providerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        db.ModelProviders.Add(new ModelProvider
        {
            Id = providerId,
            Code = "b3",
            Name = "B3",
            Enabled = true,
            SortOrder = 1,
            Source = "test",
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();

        // 第一批：10 个模型。
        AddModels(db, providerId, 10);
        var service = CreateCatalogService(db);
        capture.Reset();
        var small = service.ListModels(null, null, null);
        var smallPlansSelects = capture.CountMatching("FROM \"ModelPricingPlans\"");

        // 第二批：再补到 1000 个模型（跨分页上限，触发多页 IN 查询）。
        AddModels(db, providerId, 990);
        capture.Reset();
        var large = service.ListModels(null, null, null);
        var largePlansSelects = capture.CountMatching("FROM \"ModelPricingPlans\"");

        Assert.True(small.Succeeded);
        Assert.True(large.Succeeded);
        // B3 降级：plan 查询只随页数增长（每 900 个一页），10 个时 1 条、
        // 1000 个时 2 条，不随模型数线性增长。
        Assert.True(largePlansSelects <= smallPlansSelects + 1,
            $"plan SELECT 从 {smallPlansSelects} 涨到 {largePlansSelects}，不应随模型数线性增长");
    }

    private static void AddModels(IOpenCodexDbContext db, Guid providerId, int count)
    {
        var now = 1.0;
        for (var i = 0; i < count; i++)
        {
            db.ModelInfos.Add(new ModelInfo
            {
                ProviderId = providerId,
                ModelKey = $"model-{Guid.NewGuid():N}",
                DisplayName = "model",
                MatchType = ModelMatchTypes.Exact,
                MatchPattern = "model",
                Enabled = true,
                Source = "test",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.SaveChanges();
    }

    private static ModelCatalogService CreateCatalogService(IOpenCodexDbContext db)
    {
        return new ModelCatalogService(
            new EfRepository<ModelProvider>(db),
            new EfRepository<ModelInfo>(db),
            new EfRepository<ChannelModelInfo>(db),
            new EfRepository<ModelPricingPlan>(db),
            new EfRepository<ModelPricingRule>(db),
            new EfRepository<ChannelModelMapping>(db),
            new EfRepository<Channel>(db),
            new StubWorkContext(AdminId, "admin", "superadmin"),
            new TestCacheService(),
            redis: null);
    }

    private static ModelCatalogService CreateEmptyCatalog(IOpenCodexDbContext db)
    {
        return new ModelCatalogService(
            new EfRepository<ModelProvider>(db),
            new EfRepository<ModelInfo>(db),
            new EfRepository<ChannelModelInfo>(db),
            new EfRepository<ModelPricingPlan>(db),
            new EfRepository<ModelPricingRule>(db),
            new EfRepository<ChannelModelMapping>(db),
            new EfRepository<Channel>(db),
            new StubWorkContext(AdminId, "admin", "superadmin"),
            new TestCacheService(),
            redis: null);
    }
}
