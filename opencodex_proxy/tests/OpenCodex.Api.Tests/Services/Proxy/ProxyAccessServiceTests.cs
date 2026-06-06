using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyAccessServiceTests
{
    [Fact]
    public void AuthenticateBearerReturnsRepositoryAccessKey()
    {
        var repository = new FakeProxyAccessRepository
        {
            AccessKey = AccessKey(7, "alice")
        };
        var service = new ProxyAccessService(repository);

        var result = service.AuthenticateBearer("Bearer  ocx_secret  ");

        Assert.Equal(7, result.Id);
        Assert.Equal("alice", result.OwnerUsername);
        Assert.Equal(["ocx_secret"], repository.RawKeyCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    public void AuthenticateBearerRejectsMissingOrWrongScheme(string? authorization)
    {
        var repository = new FakeProxyAccessRepository();
        var service = new ProxyAccessService(repository);

        var exception = Assert.Throws<BadRequestException>(() => service.AuthenticateBearer(authorization));

        Assert.Equal("valid bearer api key required", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Empty(repository.RawKeyCalls);
    }

    [Fact]
    public void AuthenticateBearerRejectsUnknownKey()
    {
        var repository = new FakeProxyAccessRepository();
        var service = new ProxyAccessService(repository);

        var exception = Assert.Throws<BadRequestException>(() => service.AuthenticateBearer("Bearer missing"));

        Assert.Equal("valid bearer api key required", exception.Message);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal(["missing"], repository.RawKeyCalls);
    }

    private static AuthenticatedAccessApiKeyRecord AccessKey(long id, string owner)
    {
        return new AuthenticatedAccessApiKeyRecord(
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
            new AccessApiKeyUserRecord(owner, "user", true));
    }

    private sealed class FakeProxyAccessRepository : IProxyAccessRepository
    {
        public AuthenticatedAccessApiKeyRecord? AccessKey { get; init; }

        public List<string?> RawKeyCalls { get; } = [];

        public AuthenticatedAccessApiKeyRecord? AuthenticateAccessApiKey(string? rawKey)
        {
            RawKeyCalls.Add(rawKey);
            return AccessKey;
        }
    }
}
