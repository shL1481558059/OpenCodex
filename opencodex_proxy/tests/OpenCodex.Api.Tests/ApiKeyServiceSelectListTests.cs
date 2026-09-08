using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ApiKeyServiceSelectListTests
{
    private static readonly Guid OwnerAId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OwnerBId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid KeyAId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid KeyBId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void NonSuperadminOnlySeesOwnKeys()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "user");
        var result = service.ListApiKeySelectOptions(null, null);

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(KeyAId, option.Id);
        Assert.Equal("Alpha Key", option.Name);
    }

    [Fact]
    public void SuperadminFiltersKeysByRequestedOwner()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListApiKeySelectOptions(null, "bob");

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(KeyBId, option.Id);
        Assert.Equal("Beta Key", option.Name);
    }

    [Fact]
    public void QueryFiltersKeysByName()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListApiKeySelectOptions("beta", null);

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(KeyBId, option.Id);
    }

    [Fact]
    public void UnknownRequestedOwnerReturnsEmptyList()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListApiKeySelectOptions(null, "missing");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Payload!);
    }

    private static void Seed(string dbPath)
    {
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.Database.Migrate();
            context.Users.AddRange(
                new User { Id = OwnerAId, Username = "alice", PasswordHash = "x", Role = "user", Enabled = true },
                new User { Id = OwnerBId, Username = "bob", PasswordHash = "x", Role = "user", Enabled = true });
            context.AccessApiKeys.AddRange(
                new AccessApiKey { Id = KeyAId, OwnerUserId = OwnerAId, Name = "Alpha Key", KeyHash = "a", Enabled = true },
                new AccessApiKey { Id = KeyBId, OwnerUserId = OwnerBId, Name = "Beta Key", KeyHash = "b", Enabled = true });
            context.SaveChanges();
        }
    }

    private static ApiKeyService CreateService(
        string dbPath,
        Guid userId,
        string username,
        string role)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ApiKeyService(
            new TestWorkContext(userId, username, role),
            new EfRepository<AccessApiKey>(context),
            new EfRepository<User>(context),
            new TestCacheService());
    }

    private static string CreateDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-key-select-list-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
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
