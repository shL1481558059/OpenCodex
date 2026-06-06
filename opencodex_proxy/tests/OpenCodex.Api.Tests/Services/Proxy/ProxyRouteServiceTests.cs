using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Proxy;

public sealed class ProxyRouteServiceTests
{
    [Fact]
    public void ChooseRouteReadsChannelsForOwner()
    {
        var repository = new FakeProxyRouteRepository
        {
            Channels = [Channel("alice-chat")]
        };
        var service = new ProxyRouteService(repository);

        var route = service.ChooseRoute("alice", "gpt-5");

        Assert.Equal(["alice"], repository.OwnerCalls);
        Assert.Equal("alice-chat", route.Channel["id"]);
        Assert.Equal("gpt-5", route.OriginalModel);
        Assert.Equal("gpt-5", route.UpstreamModel);
    }

    [Fact]
    public void ChooseRouteUsesModelMappingAndUpstreamModel()
    {
        var repository = new FakeProxyRouteRepository
        {
            Channels =
            [
                Channel(
                    "chat",
                    models:
                    [
                        new Dictionary<string, object?>
                        {
                            ["model"] = "gpt-5",
                            ["upstream_model"] = "gpt-4"
                        }
                    ])
            ]
        };
        var service = new ProxyRouteService(repository);

        var route = service.ChooseRoute("admin", "gpt-5");

        Assert.Equal("chat", route.Channel["id"]);
        Assert.Equal("gpt-5", route.OriginalModel);
        Assert.Equal("gpt-4", route.UpstreamModel);
    }

    [Fact]
    public void ChooseRouteThrowsWhenNoEnabledChannelsExist()
    {
        var repository = new FakeProxyRouteRepository
        {
            Channels = [Channel("disabled", enabled: false)]
        };
        var service = new ProxyRouteService(repository);

        var exception = Assert.Throws<RoutingException>(() => service.ChooseRoute("admin", "gpt-5"));

        Assert.Equal("no enabled channels configured", exception.Message);
    }

    [Fact]
    public void ChooseRouteExpandsEnvironmentVariablesInChannelConfig()
    {
        const string variableName = "OPEN_CODEX_PROXY_ROUTE_TEST_KEY";
        Environment.SetEnvironmentVariable(variableName, "expanded-key");
        try
        {
            var repository = new FakeProxyRouteRepository
            {
                Channels = [Channel("chat", apiKey: $"${{{variableName}}}")]
            };
            var service = new ProxyRouteService(repository);

            var route = service.ChooseRoute("admin", "gpt-5");

            Assert.Equal("expanded-key", route.Channel["apikey"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    private static ChannelRecord Channel(
        string id,
        IReadOnlyList<object?>? models = null,
        string ownerUsername = "admin",
        string type = "chat",
        string apiKey = "test-key",
        bool enabled = true)
    {
        return new ChannelRecord(
            ownerUsername,
            id,
            id,
            type,
            "https://example.test/v1",
            apiKey,
            "bearer",
            new Dictionary<string, object?>(),
            30,
            1,
            new Dictionary<string, object?>(),
            models ?? [],
            enabled);
    }

    private sealed class FakeProxyRouteRepository : IProxyRouteRepository
    {
        public IReadOnlyList<ChannelRecord> Channels { get; init; } = [];

        public List<string> OwnerCalls { get; } = [];

        public IReadOnlyList<ChannelRecord> ReadChannels(string ownerUsername)
        {
            OwnerCalls.Add(ownerUsername);
            return Channels;
        }
    }
}
