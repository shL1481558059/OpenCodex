using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchToolExecutorTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{\"query\":\"test\",\"extra\":true}")]
    public async Task InvalidArguments_DoNotReserveQuota(string arguments)
    {
        await using var db = await CreateDatabaseAsync();
        var client = new RecordingSearchClient();
        var executor = CreateExecutor(db, client);

        var result = await executor.ExecuteAsync("call_1", arguments, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.False(result.DisableSearch);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, await db.TavilyKeys.AsNoTracking().Select(item => item.UsageCount).SingleAsync());
    }

    [Fact]
    public async Task Search_ReservesOnlyAvailableQuotaAndBoundsResults()
    {
        await using var db = await CreateDatabaseAsync();
        var client = new RecordingSearchClient();
        var executor = CreateExecutor(db, client);

        Assert.Equal(WebSearchModes.Simulate, executor.CurrentMode());
        Assert.Equal(0, client.Calls);
        var first = await executor.ExecuteAsync("call_1", "{\"query\":\"test\"}", CancellationToken.None);
        var second = await executor.ExecuteAsync("call_2", "{\"query\":\"test\"}", CancellationToken.None);

        Assert.Equal("completed", first.Status);
        Assert.Equal("failed", second.Status);
        Assert.True(second.DisableSearch);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, await db.TavilyKeys.AsNoTracking().Select(item => item.UsageCount).SingleAsync());
        Assert.True(first.ToolResult.Length < 40_000);
        Assert.DoesNotContain("private-test-key", first.ToolResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_RoutesAnyConfiguredProviderThroughTheClientRouter()
    {
        await using var db = await CreateDatabaseAsync();
        var tavily = await db.TavilyKeys.SingleAsync();
        tavily.Enabled = false;
        db.TavilyKeys.Add(new TavilyKey
        {
            Provider = "keenable",
            ApiKey = "private-keenable-key",
            Enabled = true,
            UsageLimit = 1
        });
        await db.SaveChangesAsync();
        var client = new RecordingSearchClient();

        var result = await CreateExecutor(db, client)
            .ExecuteAsync("call_1", "{\"query\":\"test\"}", CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal("keenable", client.LastProvider);
    }

    [Fact]
    public async Task Cancellation_DoesNotReserveQuota()
    {
        await using var db = await CreateDatabaseAsync();
        var client = new RecordingSearchClient();
        var executor = CreateExecutor(db, client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync("call_1", "{\"query\":\"test\"}", cancellation.Token));

        Assert.Equal(0, client.Calls);
        Assert.Equal(0, await db.TavilyKeys.AsNoTracking().Select(item => item.UsageCount).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentRequests_CannotReserveTheLastQuotaMoreThanOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"web-search-quota-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        await using (var bootstrap = await CreateDatabaseAsync(connectionString))
        {
        }
        var client = new RecordingSearchClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await using var db = OpenCodexDbContextFactory.Create("sqlite", connectionString);
            return await CreateExecutor(db, client).ExecuteAsync($"call_{index}", "{\"query\":\"test\"}", CancellationToken.None);
        })));

        Assert.Single(results, result => result.Status == "completed");
        Assert.Equal(1, client.Calls);
        await using var verification = OpenCodexDbContextFactory.Create("sqlite", connectionString);
        Assert.Equal(1, await verification.TavilyKeys.AsNoTracking().Select(item => item.UsageCount).SingleAsync());
    }

    private static WebSearchToolExecutor CreateExecutor(IOpenCodexDbContext db, IWebSearchClient client)
    {
        return new WebSearchToolExecutor(
            client, new EfRepository<WebSearchSettings>(db), new EfRepository<TavilyKey>(db));
    }

    private static async Task<IOpenCodexDbContext> CreateDatabaseAsync(string connectionString = "Data Source=:memory:")
    {
        var db = OpenCodexDbContextFactory.Create("sqlite", connectionString);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.WebSearchSettings.Add(new WebSearchSettings { Mode = WebSearchModes.Simulate });
        db.TavilyKeys.Add(new TavilyKey
        {
            Provider = "tavily", ApiKey = "private-test-key", Enabled = true, UsageLimit = 1
        });
        await db.SaveChangesAsync();
        return db;
    }

    private sealed class RecordingSearchClient : IWebSearchClient
    {
        public int Calls { get; private set; }
        public string? LastProvider { get; private set; }

        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key, string query, CancellationToken cancellationToken)
        {
            Calls++;
            LastProvider = key.Provider;
            var sources = Enumerable.Range(0, 10).Select(index => new Dictionary<string, object?>
            {
                ["title"] = $"Source {index}",
                ["url"] = $"https://example.com/{index}",
                ["content"] = new string('a', 10_000)
            }).ToList();
            return Task.FromResult(new WebSearchProviderResult(
                true, 200, 1, null, null, new WebSearchSummary("answer", sources, null), null));
        }
    }
}
