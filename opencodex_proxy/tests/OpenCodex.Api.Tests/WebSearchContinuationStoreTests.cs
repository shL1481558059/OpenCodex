using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.Data;
using Xunit;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchContinuationStoreTests
{
    private static readonly Guid OwnerUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task SavedResult_RestoresAcrossStoreAndContextInstances()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            CreateOwner(context);
            var store = CreateStore(context);
            await store.SaveAsync(OwnerUserId, [SearchResult()], [ClientCall()], CancellationToken.None);
        }

        using var restoreContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var restoreStore = CreateStore(restoreContext);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "ws_ocxp_test",
                    ["type"] = "web_search_call",
                    ["status"] = "completed",
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "search",
                        ["query"] = "query"
                    }
                }
            }
        };

        await restoreStore.RestoreAsync(
            payload,
            OwnerUserId,
            WebSearchRequestPolicy.InternalToolName,
            CancellationToken.None);

        var history = ListValue(payload, "input").Cast<Dictionary<string, object?>>().ToList();
        var output = Assert.Single(history, item => StringValue(item, "type") == "function_call_output");
        Assert.Contains("answer", StringValue(output, "output"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingResultForAnotherOwner_IsRestoredAsUnavailable()
    {
        var dbPath = NewDbPath();
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            CreateOwner(context);
            var store = CreateStore(context);
            await store.SaveAsync(OwnerUserId, [SearchResult()], [], CancellationToken.None);
        }

        using var restoreContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var restoreStore = CreateStore(restoreContext);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "ws_ocxp_test",
                    ["type"] = "web_search_call",
                    ["status"] = "completed",
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "search",
                        ["query"] = "query"
                    }
                }
            }
        };

        await restoreStore.RestoreAsync(
            payload,
            Guid.NewGuid(),
            WebSearchRequestPolicy.InternalToolName,
            CancellationToken.None);

        var output = ListValue(payload, "input").Cast<Dictionary<string, object?>>()
            .Single(item => StringValue(item, "type") == "function_call_output");
        Assert.Contains("unavailable", StringValue(output, "output"), StringComparison.OrdinalIgnoreCase);
    }

    private static WebSearchContinuationStore CreateStore(OpenCodex.CoreBase.Data.IOpenCodexDbContext context) =>
        new(
            new EfWebSearchContinuationRepository(context),
            NullLogger<WebSearchContinuationStore>.Instance);

    private static WebSearchToolResult SearchResult()
    {
        var result = WebSearchToolResult.FromProvider(
            "call_1",
            "query",
            new TavilyKeyDto(Guid.NewGuid(), 1, "tavily", "key", true, 1, 100, 100),
            new WebSearchProviderResult(
                true,
                200,
                1,
                null,
                null,
                new WebSearchSummary(
                    "answer",
                    [
                        new Dictionary<string, object?>
                        {
                            ["title"] = "source",
                            ["url"] = "https://example.com",
                            ["content"] = "content"
                        }
                    ],
                    null),
                null));
        result.ItemId = "ws_ocxp_test";
        return result;
    }

    private static WebSearchToolCall ClientCall() =>
        new(
            "client_1",
            0,
            "read_file",
            "{}",
            new Dictionary<string, object?>());

    private static void CreateOwner(OpenCodex.CoreBase.Data.IOpenCodexDbContext context)
    {
        context.Database.Migrate();
        context.Users.Add(new User
        {
            Id = OwnerUserId,
            Username = "web-search-owner",
            PasswordHash = "hash",
            Role = "superadmin",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        context.SaveChanges();
    }

    private static string NewDbPath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-api-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }
}
