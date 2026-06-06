using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminApiKeyServiceTests
{
    [Fact]
    public void SuperadminBlankOwnerListsAllKeys()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            Keys = [Key(1, "admin")]
        };
        var service = new AdminApiKeyService(keys);

        var result = service.ListKeys(" ", "admin", isSuperadmin: true);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("admin", Assert.Single(result.Data).OwnerUsername);
        Assert.Single(keys.ListCalls, owner => owner is null);
    }

    [Fact]
    public void RegularUserListIgnoresRequestedOwnerAndUsesSelf()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            Keys = [Key(1, "alice")]
        };
        var service = new AdminApiKeyService(keys);

        var result = service.ListKeys("bob", "alice", isSuperadmin: false);

        Assert.True(result.Succeeded);
        Assert.Single(keys.ListCalls, owner => owner == "alice");
    }

    [Fact]
    public void RegularUserCreateForcesOwnerToSelf()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            CreatedKey = Key(10, "alice") with { Key = "ocx_secret" }
        };
        var service = new AdminApiKeyService(keys);

        var result = service.CreateKey(
            new AdminApiKeyCreateCommand("bob", "Laptop"),
            "alice",
            isSuperadmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("alice", result.Data.OwnerUsername);
        Assert.Equal([("alice", "Laptop")], keys.CreateCalls);
    }

    [Fact]
    public void CreateTrimsCommandStringsAtServiceBoundary()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            CreatedKey = Key(11, "bob")
        };
        var service = new AdminApiKeyService(keys);

        var result = service.CreateKey(
            new AdminApiKeyCreateCommand(" bob ", " Laptop "),
            "admin",
            isSuperadmin: true);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal([("bob", "Laptop")], keys.CreateCalls);
    }

    [Fact]
    public void SuperadminCreateBlankOwnerFallsBackToCurrentUser()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            CreatedKey = Key(12, "admin")
        };
        var service = new AdminApiKeyService(keys);

        var result = service.CreateKey(
            new AdminApiKeyCreateCommand(" ", "Laptop"),
            "admin",
            isSuperadmin: true);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal([("admin", "Laptop")], keys.CreateCalls);
    }

    [Fact]
    public void UpdateMissingKeyMapsToNotFound()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            UpdateException = new InvalidOperationException("api key not found")
        };
        var service = new AdminApiKeyService(keys);

        var result = service.UpdateKey(
            123,
            new AdminApiKeyUpdateCommand(false),
            "alice",
            isSuperadmin: false);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminApiKeyErrorCodes.NotFound, result.Code);
        Assert.Equal("api key not found", result.Message);
        Assert.Equal([(123L, false, "alice")], keys.UpdateCalls);
    }

    [Fact]
    public void DeleteMissingKeyMapsToNotFound()
    {
        var keys = new FakeAdminApiKeyRepository
        {
            DeleteException = new InvalidOperationException("api key not found")
        };
        var service = new AdminApiKeyService(keys);

        var result = service.DeleteKey(123, "admin", isSuperadmin: true);

        Assert.False(result.Succeeded);
        Assert.Equal(AdminApiKeyErrorCodes.NotFound, result.Code);
        Assert.Equal("api key not found", result.Message);
        Assert.Equal([(123L, (string?)null)], keys.DeleteCalls);
    }

    private static AccessApiKeyRecord Key(long id, string owner)
    {
        return new AccessApiKeyRecord(
            id,
            owner,
            "Laptop",
            "ocx_prefix",
            "suffix",
            "ocx_prefix...suffix",
            true,
            1,
            2,
            null,
            null);
    }

    private sealed class FakeAdminApiKeyRepository : IAdminApiKeyRepository
    {
        public IReadOnlyList<AccessApiKeyRecord> Keys { get; init; } = [];

        public AccessApiKeyRecord? CreatedKey { get; init; }

        public InvalidOperationException? UpdateException { get; init; }

        public InvalidOperationException? DeleteException { get; init; }

        public List<string?> ListCalls { get; } = [];

        public List<(string OwnerUsername, string Name)> CreateCalls { get; } = [];

        public List<(long KeyId, bool Enabled, string? OwnerUsername)> UpdateCalls { get; } = [];

        public List<(long KeyId, string? OwnerUsername)> DeleteCalls { get; } = [];

        public IReadOnlyList<AccessApiKeyRecord> ListAccessApiKeys(string? ownerUsername)
        {
            ListCalls.Add(ownerUsername);
            return Keys;
        }

        public AccessApiKeyRecord CreateAccessApiKey(string ownerUsername, string name)
        {
            CreateCalls.Add((ownerUsername, name));
            return CreatedKey ?? Key(1, ownerUsername);
        }

        public AccessApiKeyRecord SetAccessApiKeyEnabled(long keyId, bool enabled, string? ownerUsername)
        {
            UpdateCalls.Add((keyId, enabled, ownerUsername));
            if (UpdateException is not null)
            {
                throw UpdateException;
            }

            return Key(keyId, ownerUsername ?? "admin") with { Enabled = enabled };
        }

        public void DeleteAccessApiKey(long keyId, string? ownerUsername)
        {
            DeleteCalls.Add((keyId, ownerUsername));
            if (DeleteException is not null)
            {
                throw DeleteException;
            }
        }
    }
}
