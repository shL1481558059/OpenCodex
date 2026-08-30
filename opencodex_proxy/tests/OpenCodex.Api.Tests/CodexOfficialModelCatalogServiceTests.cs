using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using OpenCodex.Api.Services;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class CodexOfficialModelCatalogServiceTests
{
    [Fact]
    public void BuildCodexModels_AppliesLengthRulesAndAppendsProxyModels()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ocxp_codex_official_models.json"),
                CreateResourceJson(),
                Encoding.UTF8);

            var environment = new TestWebHostEnvironment(root);
            var service = new CodexOfficialModelCatalogService(environment);
            var catalogByModel = new Dictionary<string, ModelInfoResponse>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom-proxy"] = CreateModelInfo("custom-proxy", "My Custom Proxy")
            };

            var result = service.BuildCodexModels(
                [new ProxyModelCapabilityDto("custom-proxy", true)],
                catalogByModel);

            Assert.Equal(
                ["gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4-mini", "codex-auto-review", "gpt-5.6-sol", "custom-proxy"],
                result.Select(model => (string?)model["slug"]));

            AssertContextAndEffectivePercent(result, "gpt-5.6-terra", 272000L, 95, 10000L);
            AssertContextAndEffectivePercent(result, "gpt-5.6-luna", 272000L, 95, 10000L);
            AssertContextAndEffectivePercent(result, "gpt-5.6-sol", 353000, 95);
            AssertContextAndEffectivePercent(result, "gpt-5.5", 1000000, 100);
            AssertContextAndEffectivePercent(result, "gpt-5.4-mini", 1000000, 100);
            AssertContextAndEffectivePercent(result, "codex-auto-review", 1000000, 100);
            AssertContextAndEffectivePercent(result, "custom-proxy", 1000000, 100);

            var proxy = Assert.Single(result, model => (string?)model["slug"] == "custom-proxy");
            Assert.Equal("My Custom Proxy", proxy["display_name"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertContextAndEffectivePercent(
        IReadOnlyList<Dictionary<string, object?>> models,
        string slug,
        object expectedContext,
        int expectedEffectivePercent,
        object? expectedTruncationLimit = null)
    {
        var model = Assert.Single(models, item => (string?)item["slug"] == slug);
        Assert.Equal(expectedContext, model["context_window"]);
        Assert.Equal(expectedContext, model["max_context_window"]);
        Assert.Equal(expectedEffectivePercent, model["effective_context_window_percent"]);

        var truncation = Assert.IsType<Dictionary<string, object?>>(model["truncation_policy"]);
        Assert.Equal(expectedTruncationLimit ?? expectedContext, truncation["limit"]);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "opencodex-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ModelInfoResponse CreateModelInfo(string modelKey, string displayName)
    {
        return new ModelInfoResponse(
            Guid.NewGuid(),
            "provider",
            Guid.NewGuid(),
            "proxy",
            "Proxy",
            null,
            modelKey,
            displayName,
            string.Empty,
            "Exact",
            modelKey,
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
            true,
            "manual",
            null,
            0,
            0);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string root)
        {
            ApplicationName = "OpenCodex.Api.Tests";
            EnvironmentName = "Development";
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new PhysicalFileProvider(root);
            WebRootFileProvider = new PhysicalFileProvider(root);
        }

        public string ApplicationName { get; set; }

        public IFileProvider WebRootFileProvider { get; set; }

        public string WebRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }

        public string ContentRootPath { get; set; }

        public string EnvironmentName { get; set; }
    }

    private static string CreateResourceJson()
    {
        return """
        {
          "models": [
            {
              "slug": "gpt-5.6-terra",
              "display_name": "GPT-5.6-Terra",
              "context_window": 272000,
              "max_context_window": 272000,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            },
            {
              "slug": "gpt-5.6-luna",
              "display_name": "GPT-5.6-Luna",
              "context_window": 272000,
              "max_context_window": 272000,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            },
            {
              "slug": "gpt-5.5",
              "display_name": "GPT-5.5",
              "context_window": 272000,
              "max_context_window": 272000,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            },
            {
              "slug": "gpt-5.4-mini",
              "display_name": "GPT-5.4-Mini",
              "context_window": 272000,
              "max_context_window": 272000,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            },
            {
              "slug": "codex-auto-review",
              "display_name": "Codex Auto Review",
              "context_window": 272000,
              "max_context_window": 272000,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            },
            {
              "slug": "gpt-5.6-sol",
              "display_name": "GPT-5.6-Sol",
              "context_window": 272000,
              "max_context_window": 272000,
              "effective_context_window_percent": 95,
              "truncation_policy": { "mode": "tokens", "limit": 10000 }
            }
          ]
        }
        """;
    }
}
