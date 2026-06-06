using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminWebSearchServiceTests
{
    [Fact]
    public void ReadConfigReturnsRepositoryConfig()
    {
        var repository = new FakeAdminWebSearchRepository
        {
            Config = Config(enabled: true)
        };
        var service = Service(repository);

        var result = service.ReadConfig();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Enabled);
        Assert.Equal(1, repository.ReadConfigCallCount);
    }

    [Fact]
    public void SaveConfigPassesBodyToRepository()
    {
        var repository = new FakeAdminWebSearchRepository
        {
            Config = Config(enabled: true)
        };
        var service = Service(repository);
        var body = new Dictionary<string, object?>
        {
            ["enabled"] = true
        };

        var result = service.SaveConfig(body);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Same(body, repository.SavedBodies.Single());
    }

    [Fact]
    public void SaveConfigMapsValidationExceptionToBusinessFailure()
    {
        var repository = new FakeAdminWebSearchRepository
        {
            SaveException = new ArgumentException("web search keys must be a list")
        };
        var service = Service(repository);

        var result = service.SaveConfig(new Dictionary<string, object?>());

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminWebSearchErrorCodes.Validation, result.Code);
        Assert.Equal("web search keys must be a list", result.Message);
    }

    [Fact]
    public void ReserveTestKeyReturnsReservedKey()
    {
        var repository = new FakeAdminWebSearchRepository
        {
            ReservedKey = Key(10)
        };
        var service = Service(repository);

        var result = service.ReserveTestKey(10);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(10, result.Data.Id);
        Assert.Equal([10L], repository.ReservedKeyIds);
    }

    [Fact]
    public void ReserveTestKeyMapsUnavailableKeyToBusinessFailure()
    {
        var repository = new FakeAdminWebSearchRepository();
        var service = Service(repository);

        var result = service.ReserveTestKey(10);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminWebSearchErrorCodes.KeyUnavailable, result.Code);
        Assert.Equal(
            "Web Search key is unavailable or has reached its usage limit",
            result.Message);
        Assert.Equal([10L], repository.ReservedKeyIds);
    }

    [Fact]
    public async Task TestKeyCallsWebSearchClientAndReturnsRefreshedConfig()
    {
        var repository = new FakeAdminWebSearchRepository
        {
            Config = Config(enabled: true),
            ReservedKey = Key(10)
        };
        var webSearchClient = new FakeWebSearchClient
        {
            Result = ProviderResult(ok: true)
        };
        var service = Service(repository, webSearchClient);

        var result = await service.TestKeyAsync(10, "OpenAI", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(10, result.Data.Key.Id);
        Assert.True(result.Data.Result.Ok);
        Assert.True(result.Data.Config.Enabled);
        Assert.Equal([10L], repository.ReservedKeyIds);
        Assert.Equal(1, repository.ReadConfigCallCount);
        var call = Assert.Single(webSearchClient.Calls);
        Assert.Equal("tavily", call.Key.Provider);
        Assert.Equal("tvly-test", call.Key.Key);
        Assert.Equal("OpenAI", call.Query);
    }

    [Fact]
    public async Task TestKeyMapsUnavailableKeyWithoutCallingWebSearchClient()
    {
        var repository = new FakeAdminWebSearchRepository();
        var webSearchClient = new FakeWebSearchClient();
        var service = Service(repository, webSearchClient);

        var result = await service.TestKeyAsync(10, "OpenAI", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminWebSearchErrorCodes.KeyUnavailable, result.Code);
        Assert.Equal(
            "Web Search key is unavailable or has reached its usage limit",
            result.Message);
        Assert.Equal([10L], repository.ReservedKeyIds);
        Assert.Empty(webSearchClient.Calls);
        Assert.Equal(0, repository.ReadConfigCallCount);
    }

    private static AdminWebSearchService Service(
        FakeAdminWebSearchRepository repository,
        FakeWebSearchClient? webSearchClient = null)
    {
        return new AdminWebSearchService(
            repository,
            webSearchClient ?? new FakeWebSearchClient());
    }

    private static WebSearchConfigRecord Config(bool enabled = false)
    {
        return new WebSearchConfigRecord(
            enabled,
            ["tavily"],
            1000,
            []);
    }

    private static TavilyKeyRecord Key(long id)
    {
        return new TavilyKeyRecord(
            id,
            0,
            "tavily",
            "tvly-test",
            true,
            1,
            1000,
            1000);
    }

    private static WebSearchProviderResult ProviderResult(bool ok)
    {
        return new WebSearchProviderResult(
            ok,
            ok ? 200 : 500,
            7,
            ok ? null : "http_error",
            ok ? null : "Tavily returned HTTP 500",
            new WebSearchSummary(ok ? "ok" : string.Empty, [], ok ? null : "failed"),
            new Dictionary<string, object?> { ["answer"] = ok ? "ok" : string.Empty });
    }

    private sealed class FakeWebSearchClient : IWebSearchClient
    {
        public WebSearchProviderResult Result { get; init; } = ProviderResult(ok: true);

        public List<(WebSearchProviderKey Key, string Query)> Calls { get; } = [];

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            Calls.Add((key, query));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeAdminWebSearchRepository : IAdminWebSearchRepository
    {
        public WebSearchConfigRecord Config { get; init; } = Config();

        public ArgumentException? SaveException { get; init; }

        public TavilyKeyRecord? ReservedKey { get; init; }

        public int ReadConfigCallCount { get; private set; }

        public List<IReadOnlyDictionary<string, object?>> SavedBodies { get; } = [];

        public List<long> ReservedKeyIds { get; } = [];

        public WebSearchConfigRecord ReadWebSearchConfig()
        {
            ReadConfigCallCount++;
            return Config;
        }

        public WebSearchConfigRecord ReplaceWebSearchConfig(
            IReadOnlyDictionary<string, object?> config)
        {
            SavedBodies.Add(config);
            if (SaveException is not null)
            {
                throw SaveException;
            }

            return Config;
        }

        public TavilyKeyRecord? ReserveTavilyKeyById(long keyId)
        {
            ReservedKeyIds.Add(keyId);
            return ReservedKey;
        }
    }
}
