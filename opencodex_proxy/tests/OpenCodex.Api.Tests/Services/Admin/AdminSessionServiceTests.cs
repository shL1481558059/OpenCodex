using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminSessionServiceTests
{
    [Fact]
    public void RequireUserRejectsMissingSessionUser()
    {
        var repository = new FakeUserRepository();
        var service = new AdminSessionService(repository);

        var exception = Assert.Throws<BadRequestException>(() => service.RequireUser(null));

        Assert.Equal("admin authentication required", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Empty(repository.Usernames);
    }

    [Fact]
    public void RequireUserRefreshesSessionUserFromRepository()
    {
        var repository = new FakeUserRepository
        {
            User = new UserEntity("alice", "hash", "superadmin", true, 1, 2)
        };
        var service = new AdminSessionService(repository);

        var result = service.RequireUser(new AdminSessionUser("alice", "user", true));

        Assert.Equal("alice", result.Username);
        Assert.Equal("superadmin", result.Role);
        Assert.True(result.Enabled);
        Assert.Equal(["alice"], repository.Usernames);
    }

    [Fact]
    public void RequireUserRejectsMissingStoredUser()
    {
        var repository = new FakeUserRepository();
        var service = new AdminSessionService(repository);

        var exception = Assert.Throws<BadRequestException>(() =>
            service.RequireUser(new AdminSessionUser("missing", "user", true)));

        Assert.Equal("admin authentication required", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal(["missing"], repository.Usernames);
    }

    [Fact]
    public void RequireUserRejectsDisabledStoredUser()
    {
        var repository = new FakeUserRepository
        {
            User = new UserEntity("alice", "hash", "user", false, 1, 2)
        };
        var service = new AdminSessionService(repository);

        var exception = Assert.Throws<BadRequestException>(() =>
            service.RequireUser(new AdminSessionUser("alice", "user", true)));

        Assert.Equal("admin authentication required", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    [Fact]
    public void RequireSuperadminRejectsRegularUser()
    {
        var repository = new FakeUserRepository
        {
            User = new UserEntity("alice", "hash", "user", true, 1, 2)
        };
        var service = new AdminSessionService(repository);

        var exception = Assert.Throws<BadRequestException>(() =>
            service.RequireSuperadmin(new AdminSessionUser("alice", "user", true)));

        Assert.Equal("superadmin required", exception.Message);
        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    [Fact]
    public void RequireSuperadminReturnsSuperadmin()
    {
        var repository = new FakeUserRepository
        {
            User = new UserEntity("admin", "hash", "superadmin", true, 1, 2)
        };
        var service = new AdminSessionService(repository);

        var result = service.RequireSuperadmin(new AdminSessionUser("admin", "superadmin", true));

        Assert.Equal("admin", result.Username);
        Assert.Equal("superadmin", result.Role);
    }

    private sealed class FakeUserRepository : IRepository<UserEntity>
    {
        public UserEntity? User { get; init; }

        public List<string> Usernames { get; } = [];

        public UserEntity? GetById(object id)
        {
            var username = Assert.IsType<string>(id);
            Usernames.Add(username);
            return User;
        }

        public IReadOnlyList<UserEntity> ListAll()
        {
            return User is null ? [] : [User];
        }
    }
}
