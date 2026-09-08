using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Services;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ChannelServiceSelectListTests
{
    private static readonly Guid OwnerAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnerBId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ChannelAId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ChannelBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void NonSuperadminOnlySeesOwnChannels()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "user");
        var result = service.ListChannelSelectOptions(null, null);

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(ChannelAId, option.Id);
        Assert.Equal("Alpha", option.Name);
    }

    [Fact]
    public void SuperadminFiltersChannelsByRequestedOwner()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListChannelSelectOptions(null, "bob");

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(ChannelBId, option.Id);
        Assert.Equal("Beta", option.Name);
    }

    [Fact]
    public void SuperadminWithoutOwnerSeesAllChannels()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListChannelSelectOptions(null, null);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Payload!.Count);
    }

    [Fact]
    public void QueryFiltersChannelByName()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListChannelSelectOptions("bet", null);

        Assert.True(result.Succeeded);
        var option = Assert.Single(result.Payload!);
        Assert.Equal(ChannelBId, option.Id);
    }

    [Fact]
    public void UnknownRequestedOwnerReturnsEmptyList()
    {
        var dbPath = CreateDbPath();
        Seed(dbPath);

        var service = CreateService(dbPath, OwnerAId, "alice", "superadmin");
        var result = service.ListChannelSelectOptions(null, "missing");

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
            context.Channels.AddRange(
                new Channel { Id = ChannelAId, OwnerUserId = OwnerAId, Name = "Alpha", Enabled = true },
                new Channel { Id = ChannelBId, OwnerUserId = OwnerBId, Name = "Beta", Enabled = true });
            context.SaveChanges();
        }
    }

    private static ChannelService CreateService(
        string dbPath,
        Guid userId,
        string username,
        string role)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        return new ChannelService(
            null!,
            null!,
            null!,
            new TestWorkContext(userId, username, role),
            new EfRepository<Channel>(context),
            new EfRepository<User>(context),
            new EfRepository<ChannelModelMapping>(context),
            new EfRepository<VisionTransferSettings>(context),
            new TestCacheService(),
            new MemoryCache(new MemoryCacheOptions()));
    }

    private static string CreateDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-channel-select-list-tests",
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
