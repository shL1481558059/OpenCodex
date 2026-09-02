using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace OpenCodex.Api.Services;

public sealed class CodexOfficialModelCatalogService : ICodexOfficialModelCatalogService
{
    private const string ResourceFileName = "ocxp_codex_official_models.json";
    private const int OneMillion = 1_000_000;
    private const int SolContextWindow = 353_000;

    private readonly string _resourcePath;
    private readonly Lazy<IReadOnlyList<Dictionary<string, object?>>> _baseModels;

    public CodexOfficialModelCatalogService(IWebHostEnvironment environment)
    {
        _resourcePath = ResolveResourcePath(environment);
        _baseModels = new Lazy<IReadOnlyList<Dictionary<string, object?>>>(LoadBaseModels);
    }

    public IReadOnlyList<Dictionary<string, object?>> BuildCodexGptModels()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Dictionary<string, object?>>();

        foreach (var model in _baseModels.Value)
        {
            var slug = ReadString(model, "slug");
            if (string.IsNullOrWhiteSpace(slug)
                || !slug.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(slug))
            {
                continue;
            }

            var clone = Clone(model);
            ApplyBaseModelLengthRules(clone, slug);
            result.Add(clone);
        }

        return result;
    }

    private IReadOnlyList<Dictionary<string, object?>> LoadBaseModels()
    {
        if (!File.Exists(_resourcePath))
        {
            throw new FileNotFoundException(
                $"Codex model catalog resource does not exist: {_resourcePath}",
                _resourcePath);
        }

        var root = JsonNode.Parse(File.ReadAllText(_resourcePath))?.AsObject();
        if (root is null || root["models"] is not JsonArray models)
        {
            throw new InvalidDataException(
                $"Codex model catalog resource is missing a models array: {_resourcePath}");
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var node in models)
        {
            if (node is JsonObject model)
            {
                result.Add(JsonObjectToDictionary(model));
            }
        }

        return result;
    }

    private static string ResolveResourcePath(IWebHostEnvironment environment)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(environment.WebRootPath))
        {
            candidates.Add(Path.Combine(environment.WebRootPath, ResourceFileName));
        }

        if (!string.IsNullOrWhiteSpace(environment.ContentRootPath))
        {
            candidates.Add(Path.Combine(environment.ContentRootPath, "wwwroot", ResourceFileName));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "wwwroot", ResourceFileName));
        return candidates.FirstOrDefault(File.Exists)
            ?? candidates[0]
            ?? throw new InvalidOperationException("Unable to resolve Codex model catalog resource path.");
    }

    private static void ApplyBaseModelLengthRules(Dictionary<string, object?> model, string slug)
    {
        if (!slug.StartsWith("gpt-5.6-", StringComparison.OrdinalIgnoreCase))
        {
            ApplyOneMillionContext(model);
            return;
        }

        model["effective_context_window_percent"] = 95;
        if (slug.Equals("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
        {
            ApplyContextWindow(model, SolContextWindow, 95);
        }
    }

    private static void ApplyOneMillionContext(Dictionary<string, object?> model)
    {
        ApplyContextWindow(model, OneMillion, 100);
    }

    private static void ApplyContextWindow(
        Dictionary<string, object?> model,
        int contextWindow,
        int effectivePercent)
    {
        model["context_window"] = contextWindow;
        model["max_context_window"] = contextWindow;

        if (model.TryGetValue("truncation_policy", out var value)
            && value is Dictionary<string, object?> truncation)
        {
            truncation["limit"] = contextWindow;
        }
        else
        {
            model["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = contextWindow
            };
        }

        model["effective_context_window_percent"] = effectivePercent;
    }

    private static string? ReadString(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value as string : null;
    }

    private static Dictionary<string, object?> JsonObjectToDictionary(JsonObject source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in source)
        {
            result[property.Key] = JsonValueToObject(property.Value);
        }

        return result;
    }

    private static object? JsonValueToObject(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            return JsonObjectToDictionary(obj);
        }

        if (node is JsonArray array)
        {
            var list = new List<object?>();
            foreach (var item in array)
            {
                list.Add(JsonValueToObject(item));
            }

            return list;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<double>(out var fraction))
            {
                return fraction;
            }

            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
        }

        return node.ToJsonString();
    }

    private static Dictionary<string, object?> Clone(Dictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in source)
        {
            result[property.Key] = CloneValue(property.Value);
        }

        return result;
    }

    private static object? CloneValue(object? value)
    {
        if (value is Dictionary<string, object?> dict)
        {
            return Clone(dict);
        }

        if (value is List<object?> list)
        {
            var result = new List<object?>();
            foreach (var item in list)
            {
                result.Add(CloneValue(item));
            }

            return result;
        }

        return value;
    }
}
