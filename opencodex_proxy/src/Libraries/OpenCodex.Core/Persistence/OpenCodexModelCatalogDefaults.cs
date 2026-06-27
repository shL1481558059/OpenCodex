using System.Text.Json;
using OpenCodex.Core.Domain;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexModelCatalogDefaults
{
    public static IReadOnlyList<DefaultModelProvider> Providers()
    {
        return
        [
            new DefaultModelProvider("openai", "OpenAI", 10),
            new DefaultModelProvider("anthropic", "Anthropic", 20),
            new DefaultModelProvider("google", "Google", 30),
            new DefaultModelProvider("deepseek", "DeepSeek", 40),
            new DefaultModelProvider("qwen", "Qwen", 50),
            new DefaultModelProvider("moonshot", "Moonshot", 60),
            new DefaultModelProvider("xai", "xAI", 70),
            new DefaultModelProvider("mistral", "Mistral", 80),
            new DefaultModelProvider("groq", "Groq", 90),
            new DefaultModelProvider("openrouter", "OpenRouter", 100),
            new DefaultModelProvider("zhipu", "智谱", 110),
            new DefaultModelProvider("unknown", "Unknown", 1000)
        ];
    }

    public static IReadOnlyList<DefaultModelInfo> Models()
    {
        return
        [
            Default("openai", "gpt-5", "GPT-5", "gpt-5", true, 256000, 1.25m, 10m, 1.25m, 0.125m),
            Default("openai", "gpt-4o", "GPT-4o", "gpt-4o", true, 128000, 2.5m, 10m, 2.5m, 1.25m),
            Default("openai", "o3", "o3", "o3", true, 200000, 2m, 8m, 2m, 0.5m),
            Default("anthropic", "claude-sonnet-4", "Claude Sonnet 4", "claude-sonnet-4", true, 200000, 3m, 15m, 3.75m, 0.3m),
            Default("anthropic", "claude-opus-4", "Claude Opus 4", "claude-opus-4", true, 200000, 15m, 75m, 18.75m, 1.5m),
            Default("google", "gemini-2.5-pro", "Gemini 2.5 Pro", "gemini-2.5-pro", true, 1048576, 1.25m, 10m, 1.25m, 0.31m),
            Default("deepseek", "deepseek-chat", "DeepSeek Chat", "deepseek-chat", false, 64000, 0.27m, 1.1m, 0.27m, 0.07m),
            Default("qwen", "qwen-plus", "Qwen Plus", "qwen-plus", true, 131072, 0.4m, 1.2m, 0.4m, 0.1m),
            Default("moonshot", "kimi-k2", "Kimi K2", "kimi-k2", true, 128000, 0.6m, 2.5m, 0.6m, 0.15m),
            Default("xai", "grok-4", "Grok 4", "grok-4", true, 256000, 3m, 15m, 3m, 0.75m),
            Default("mistral", "mistral-large-latest", "Mistral Large", "mistral-large-latest", true, 128000, 2m, 6m, 2m, 0.5m),
            Default("groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile", "llama-3.3-70b-versatile", false, 131072, 0.59m, 0.79m, 0.59m, 0.15m),
            Default("openrouter", "openrouter/auto", "OpenRouter Auto", "openrouter/auto", true, 128000, 0m, 0m, 0m, 0m),
            Default("zhipu", "glm-5.2", "GLM-5.2", "glm-5.2", false, 1_000_000, 8m, 28m, 0m, 2m, currency: "CNY"),
            Default("zhipu", "glm-4.5", "GLM-4.5", "glm-4.5", true, 128000, 0.5m, 2m, 0.5m, 0.125m, currency: "CNY")
        ];
    }

    public static string CapabilitiesJson(bool supportsImage, int contextWindow)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["supports_image"] = supportsImage,
            ["context_window"] = contextWindow,
            ["supports_parallel_tool_calls"] = true,
            ["supports_reasoning_summaries"] = true
        });
    }

    public static string CatalogJson(
        string modelKey,
        string displayName,
        bool supportsImage,
        int contextWindow)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["slug"] = modelKey,
            ["display_name"] = displayName,
            ["description"] = $"{displayName} routed through OpenCodex.",
            ["visibility"] = "list",
            ["supported_in_api"] = true,
            ["priority"] = 100,
            ["shell_type"] = "shell_command",
            ["support_verbosity"] = true,
            ["default_verbosity"] = "medium",
            ["apply_patch_tool_type"] = "freeform",
            ["web_search_tool_type"] = "text",
            ["input_modalities"] = supportsImage
                ? new List<object?> { "text", "image" }
                : new List<object?> { "text" },
            ["supports_image_detail_original"] = supportsImage,
            ["context_window"] = contextWindow,
            ["max_context_window"] = contextWindow,
            ["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = contextWindow
            },
            ["supports_parallel_tool_calls"] = true,
            ["supports_reasoning_summaries"] = true,
            ["default_reasoning_summary"] = "short",
            ["reasoning_summary_format"] = "text"
        });
    }

    private static DefaultModelInfo Default(
        string providerCode,
        string modelKey,
        string displayName,
        string matchPattern,
        bool supportsImage,
        int contextWindow,
        decimal inputPrice,
        decimal outputPrice,
        decimal cacheWritePrice,
        decimal cacheReadPrice,
        string currency = "USD")
    {
        return new DefaultModelInfo(
            providerCode,
            modelKey,
            displayName,
            $"{displayName} routed through OpenCodex.",
            ModelMatchTypes.Exact,
            matchPattern,
            CapabilitiesJson(supportsImage, contextWindow),
            CatalogJson(modelKey, displayName, supportsImage, contextWindow),
            currency,
            inputPrice,
            outputPrice,
            cacheWritePrice,
            cacheReadPrice);
    }
}

public sealed class DefaultModelProvider
{
    public DefaultModelProvider(string code, string name, int sortOrder)
    {
        Code = code;
        Name = name;
        SortOrder = sortOrder;
    }

    public string Code { get; }

    public string Name { get; }

    public int SortOrder { get; }
}

public sealed class DefaultModelInfo
{
    public DefaultModelInfo(
        string providerCode,
        string modelKey,
        string displayName,
        string description,
        string matchType,
        string matchPattern,
        string capabilitiesJson,
        string catalogJson,
        string currency,
        decimal inputPrice,
        decimal outputPrice,
        decimal cacheWritePrice,
        decimal cacheReadPrice)
    {
        ProviderCode = providerCode;
        ModelKey = modelKey;
        DisplayName = displayName;
        Description = description;
        MatchType = matchType;
        MatchPattern = matchPattern;
        CapabilitiesJson = capabilitiesJson;
        CatalogJson = catalogJson;
        Currency = currency;
        InputPrice = inputPrice;
        OutputPrice = outputPrice;
        CacheWritePrice = cacheWritePrice;
        CacheReadPrice = cacheReadPrice;
    }

    public string ProviderCode { get; }

    public string ModelKey { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string MatchType { get; }

    public string MatchPattern { get; }

    public string CapabilitiesJson { get; }

    public string CatalogJson { get; }

    public string Currency { get; }

    public decimal InputPrice { get; }

    public decimal OutputPrice { get; }

    public decimal CacheWritePrice { get; }

    public decimal CacheReadPrice { get; }
}
