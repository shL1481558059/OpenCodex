using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Api.Services;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class CodexOfficialModelCatalogServiceTests
{
    [Fact]
    public void BuildCodexGptModelsNormalizesMissingRequiredFields()
    {
        var resourceDirectory = Directory.CreateTempSubdirectory("opencodex-model-catalog-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(resourceDirectory, "ocxp_codex_official_models.json"),
                """
                {
                  "models": [
                    {
                      "slug": "gpt-5.6-test",
                      "display_name": "GPT Test",
                      "context_window": 123000,
                      "input_modalities": [
                        "text",
                        "video"
                      ],
                      "supported_reasoning_levels": [
                        { "effort": "low" },
                        { "effort": "high" }
                      ],
                      "default_reasoning_level": "medium"
                    }
                  ]
                }
                """);

            var service = new CodexOfficialModelCatalogService(
                new TestWebHostEnvironment
                {
                    WebRootPath = resourceDirectory,
                    ContentRootPath = resourceDirectory
                },
                NullLogger<CodexOfficialModelCatalogService>.Instance);

            var model = Assert.Single(service.BuildCodexGptModels());

            Assert.True((bool)model["supports_reasoning_summaries"]!);
            Assert.True((bool)model["supports_reasoning_summary_parameter"]!);
            Assert.Equal(
                new List<object?> { "text" },
                Assert.IsType<List<object?>>(model["input_modalities"]));
            Assert.Equal("low", model["default_reasoning_level"]);
            Assert.Equal(123000L, model["context_window"]);
            Assert.Equal(123000L, model["max_context_window"]);
            Assert.False((bool)model["supports_image_detail_original"]!);
            Assert.NotEmpty(Assert.IsType<string>(model["base_instructions"]));
        }
        finally
        {
            Directory.Delete(resourceDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildCodexGptModelsReturnsLatestOfficialCatalog()
    {
        var resourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "opencodex_proxy",
            "src",
            "Presentation",
            "OpenCodex.Api",
            "wwwroot");
        var service = new CodexOfficialModelCatalogService(
            new TestWebHostEnvironment
            {
                WebRootPath = resourceDirectory,
                ContentRootPath = resourceDirectory
            },
            NullLogger<CodexOfficialModelCatalogService>.Instance);

        var models = service.BuildCodexGptModels();
        var slugs = models
            .Select(model => model["slug"])
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gpt-5.6-sol", slugs);
        Assert.Contains("gpt-6-astra", slugs);
        Assert.Contains("gpt-daybreak-blue-latest", slugs);
        Assert.All(models, model =>
        {
            Assert.True((bool)model["supports_reasoning_summaries"]!);
            Assert.True((bool)model["supports_reasoning_summary_parameter"]!);
            Assert.NotEmpty(Assert.IsType<string>(model["base_instructions"]));
            Assert.NotEmpty(Assert.IsType<List<object?>>(model["input_modalities"]));
        });

        var gpt55 = Assert.Single(models, model => "gpt-5.5".Equals(model["slug"]));
        Assert.Equal(1_000_000, gpt55["context_window"]);
        Assert.Equal(1_000_000, gpt55["max_context_window"]);
        var sol = Assert.Single(models, model => "gpt-5.6-sol".Equals(model["slug"]));
        Assert.Equal(353_000, sol["context_window"]);
        Assert.Equal(353_000, sol["max_context_window"]);
        var solTruncation = Assert.IsType<Dictionary<string, object?>>(sol["truncation_policy"]);
        Assert.Equal(353_000, solTruncation["limit"]);
    }

    [Fact]
    public void OfficialCatalogTemplateContainsLatestModelsAndReasoningCompatibilityFields()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resourcePath = Path.Combine(
            repositoryRoot,
            "opencodex_proxy",
            "src",
            "Presentation",
            "OpenCodex.Api",
            "wwwroot",
            "ocxp_codex_official_models.json");
        using var document = JsonDocument.Parse(File.ReadAllText(resourcePath));
        var models = document.RootElement.GetProperty("models").EnumerateArray().ToList();
        var slugs = models
            .Select(model => model.GetProperty("slug").GetString())
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("gpt-6-astra", slugs);
        Assert.Contains("gpt-6-sol", slugs);
        Assert.Contains("gpt-6-luna", slugs);
        Assert.Contains("gpt-daybreak-blue-latest", slugs);
        Assert.Contains("gpt-daybreak-red-latest", slugs);
        Assert.Contains("gpt-5.4", slugs);
        Assert.Contains("gpt-5.4-mini", slugs);

        Assert.All(models, model =>
        {
            var slug = model.GetProperty("slug").GetString();
            Assert.True(
                model.TryGetProperty("supports_reasoning_summaries", out var summaries)
                && summaries.GetBoolean(),
                $"{slug} is missing supports_reasoning_summaries.");
            Assert.True(
                model.TryGetProperty("supports_reasoning_summary_parameter", out var summaryParameter)
                && summaryParameter.GetBoolean(),
                $"{slug} is missing supports_reasoning_summary_parameter.");
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var marker = Path.Combine(
                directory.FullName,
                "opencodex_proxy",
                "src",
                "Presentation",
                "OpenCodex.Api",
                "wwwroot",
                "ocxp_codex_official_models.json");
            if (File.Exists(marker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the OpenCodex repository root.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "OpenCodex.Api.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
