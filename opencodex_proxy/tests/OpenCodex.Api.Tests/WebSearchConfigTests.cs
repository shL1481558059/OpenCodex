using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.WebSearch;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchConfigTests
{
    [Fact]
    public void ReadConfig_DefaultsMissingSettingsStrictToTrue()
    {
        using var db = CreateDatabase();
        var service = CreateService(db);

        var result = service.ReadConfig();

        Assert.True(result.Succeeded);
        Assert.True(result.Payload!.Strict);
    }

    [Fact]
    public void ReadConfig_ReturnsPersistedStrict()
    {
        using var db = CreateDatabase();
        db.WebSearchSettings.Add(new WebSearchSettings
        {
            Mode = WebSearchModes.Simulate,
            Strict = false,
            KeyUsageLimit = 1000,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();

        var result = CreateService(db).ReadConfig();

        Assert.True(result.Succeeded);
        Assert.False(result.Payload!.Strict);
        Assert.Equal(WebSearchModes.Simulate, result.Payload.Mode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveConfig_PersistsAndReadsBackStrict(bool strict)
    {
        using var db = CreateDatabase();
        var service = CreateService(db);

        var saved = service.SaveConfig(new Dictionary<string, object?>
        {
            ["mode"] = WebSearchModes.Simulate,
            ["strict"] = strict,
            ["keys"] = new List<object?>()
        });

        Assert.True(saved.Succeeded);
        Assert.Equal(strict, saved.Payload!.Strict);
        Assert.Equal(strict, db.WebSearchSettings.Single().Strict);
        Assert.Equal(strict, service.ReadConfig().Payload!.Strict);
    }

    [Theory]
    [MemberData(nameof(MissingStrictPayloads))]
    public void SaveConfig_MissingOrNullStrict_DefaultsToTrue(Dictionary<string, object?> body)
    {
        using var db = CreateDatabase();
        db.WebSearchSettings.Add(new WebSearchSettings
        {
            Mode = WebSearchModes.Convert,
            Strict = false,
            KeyUsageLimit = 1000,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        db.SaveChanges();

        var result = CreateService(db).SaveConfig(body);

        Assert.True(result.Succeeded);
        Assert.True(result.Payload!.Strict);
        Assert.True(db.WebSearchSettings.Single().Strict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportConfig_PersistsAndReadsBackStrict(bool strict)
    {
        using var db = CreateDatabase();
        var service = CreateService(db);

        var imported = service.ImportConfig(new Dictionary<string, object?>
        {
            ["mode"] = WebSearchModes.Simulate,
            ["strict"] = strict,
            ["keys"] = new List<object?>()
        });

        Assert.True(imported.Succeeded);
        Assert.Equal(strict, imported.Payload!.Strict);
        Assert.Equal(strict, db.WebSearchSettings.Single().Strict);
        Assert.Equal(strict, service.ReadConfig().Payload!.Strict);
    }

    [Fact]
    public void ImportConfig_MissingStrict_DefaultsToTrue()
    {
        using var db = CreateDatabase();
        var result = CreateService(db).ImportConfig(new Dictionary<string, object?>
        {
            ["mode"] = WebSearchModes.Convert,
            ["keys"] = new List<object?>()
        });

        Assert.True(result.Succeeded);
        Assert.True(result.Payload!.Strict);
    }

    [Fact]
    public void SaveConfig_RejectsInvalidStrict()
    {
        using var db = CreateDatabase();
        var result = CreateService(db).SaveConfig(new Dictionary<string, object?>
        {
            ["mode"] = WebSearchModes.Convert,
            ["strict"] = "not-a-bool",
            ["keys"] = new List<object?>()
        });

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Code);
        Assert.Contains("strict", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigRequest_ToDictionary_IncludesStrictWhenSet()
    {
        var request = new WebSearchConfigRequest
        {
            Mode = WebSearchModes.Simulate,
            Strict = false,
            Keys = []
        };

        var dictionary = request.ToDictionary();

        Assert.Equal(false, dictionary["strict"]);
    }

    [Fact]
    public void ConfigRequest_ToDictionary_OmitsMissingStrict()
    {
        var request = new WebSearchConfigRequest
        {
            Mode = WebSearchModes.Convert,
            Keys = []
        };

        Assert.False(request.ToDictionary().ContainsKey("strict"));
    }

    [Fact]
    public void ConfigResponse_FromDto_RoundTripsStrict()
    {
        var dto = new WebSearchConfigDto(
            WebSearchModes.Simulate,
            false,
            ["tavily"],
            1000,
            Array.Empty<TavilyKeyDto>());

        var response = WebSearchConfigResponse.From(dto);

        Assert.False(response.Strict);
        Assert.Equal(WebSearchModes.Simulate, response.Mode);
    }

    public static TheoryData<Dictionary<string, object?>> MissingStrictPayloads()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["mode"] = WebSearchModes.Convert,
                ["keys"] = new List<object?>()
            },
            new Dictionary<string, object?>
            {
                ["mode"] = WebSearchModes.Convert,
                ["strict"] = null,
                ["keys"] = new List<object?>()
            }
        ];
    }

    private static WebSearchService CreateService(IOpenCodexDbContext db)
    {
        return new WebSearchService(
            new StubWebSearchClient(),
            new EfRepository<WebSearchSettings>(db),
            new EfRepository<TavilyKey>(db));
    }

    private static IOpenCodexDbContext CreateDatabase()
    {
        var db = OpenCodexDbContextFactory.Create("sqlite", "Data Source=:memory:");
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private sealed class StubWebSearchClient : IWebSearchClient
    {
        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSearchProviderResult(
                true,
                200,
                0,
                null,
                null,
                new WebSearchSummary(string.Empty, [], null),
                null));
        }
    }
}
