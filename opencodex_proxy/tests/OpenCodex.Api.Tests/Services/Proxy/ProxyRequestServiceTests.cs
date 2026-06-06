using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyRequestServiceTests
{
    [Fact]
    public void StartRequestReturnsRuntimeDefaultsAndGeneratedRequestId()
    {
        var service = new ProxyRequestService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "root", "pw", 45)),
            new FakeProxyAccessService());

        var state = service.StartRequest();

        Assert.Equal("root", state.DefaultOwnerUsername);
        Assert.Equal(45, state.DefaultTimeout);
        Assert.Matches("^[0-9a-f]{12}$", state.RequestId);
    }

    [Fact]
    public void AuthenticateAccessKeyPassesAuthorizationHeader()
    {
        var access = new FakeProxyAccessService
        {
            AccessKey = AccessKey(9, "alice")
        };
        var service = new ProxyRequestService(
            new FakeSettingsProvider(new OpenCodexRuntimeSettings("test.db", "admin", "pw", 120)),
            access);

        var result = service.AuthenticateAccessKey("Bearer ocx_secret");

        Assert.Equal(9, result.Id);
        Assert.Equal("alice", result.OwnerUsername);
        Assert.Equal(["Bearer ocx_secret"], access.AuthorizationHeaderCalls);
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

    private sealed class FakeProxyAccessService : IProxyAccessService
    {
        public AuthenticatedAccessApiKeyRecord AccessKey { get; init; } = AccessKey(1, "admin");

        public List<string?> AuthorizationHeaderCalls { get; } = [];

        public AuthenticatedAccessApiKeyRecord AuthenticateBearer(string? authorizationHeader)
        {
            AuthorizationHeaderCalls.Add(authorizationHeader);
            return AccessKey;
        }
    }
}
