using System.Text.Json;

namespace OpenCodex.Core.Services;

public static class CodexModelCatalogContract
{
    private const int DefaultContextWindow = 256_000;

    private static readonly HashSet<string> AllowedInputModalities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "text",
            "image",
            "audio"
        };

    public static void Apply(Dictionary<string, object?> catalog, bool supportsImage)
    {
        EnsurePositiveLong(catalog, "context_window", DefaultContextWindow);
        var contextWindow = ReadPositiveLong(catalog, "context_window") ?? DefaultContextWindow;
        EnsurePositiveLong(catalog, "max_context_window", contextWindow);

        var supportsImageDetail =
            (ReadBoolean(catalog, "supports_image_detail_original") ?? supportsImage)
            || ReadInputModalities(catalog).Contains("image", StringComparer.OrdinalIgnoreCase);
        catalog["supports_image_detail_original"] = supportsImageDetail;
        NormalizeInputModalities(catalog, supportsImageDetail);

        NormalizeReasoningLevels(catalog);
        NormalizeDefaultReasoningLevel(catalog);
        NormalizeDefaultReasoningSummary(catalog);

        EnsureBoolean(catalog, "supports_reasoning_summaries", true);
        EnsureBoolean(catalog, "supports_reasoning_summary_parameter", true);
        EnsureBoolean(catalog, "supports_search_tool", true);
        EnsureBoolean(catalog, "use_responses_lite", false);
        EnsureBoolean(catalog, "node_repl_disabled", false);
        EnsureBoolean(catalog, "node_repl_auto_review_required", false);
        EnsureBoolean(catalog, "include_apps_usage_instructions", true);
        EnsureBoolean(catalog, "include_plugin_usage_instructions", true);
        EnsureBoolean(catalog, "include_skills_usage_instructions", true);
        EnsureBoolean(catalog, "supports_parallel_tool_calls", true);
        EnsureBoolean(catalog, "support_verbosity", true);
        EnsureString(catalog, "reasoning_summary_format", "text");
        EnsureString(catalog, "default_verbosity", "medium");
        EnsureString(catalog, "apply_patch_tool_type", "freeform");
        EnsureString(catalog, "web_search_tool_type", "text");

        catalog.TryAdd("experimental_supported_tools", new List<object?>());
        NormalizeServiceTiers(catalog);
        NormalizeAdditionalSpeedTiers(catalog);
        EnsureInteger(catalog, "effective_context_window_percent", 100, 1, 100);
        catalog.TryAdd("availability_nux", null);
        catalog.TryAdd("upgrade", null);

        NormalizeTruncationPolicy(catalog, contextWindow);
        EnsureInstructions(catalog);
    }

    private static void NormalizeInputModalities(
        Dictionary<string, object?> catalog,
        bool supportsImage)
    {
        var modalities = ReadInputModalities(catalog)
            .Where(modality => AllowedInputModalities.Contains(modality))
            .Select(modality => modality.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<object?>()
            .ToList();

        if (modalities.Count == 0)
        {
            modalities =
            [
                "text",
                .. supportsImage ? new object?[] { "image" } : []
            ];
        }

        catalog["input_modalities"] = modalities;
    }

    private static List<string> ReadInputModalities(Dictionary<string, object?> catalog)
    {
        if (!catalog.TryGetValue("input_modalities", out var value)
            || value is not IEnumerable<object?> values)
        {
            return [];
        }

        return values
            .Select(ReadString)
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static void NormalizeReasoningLevels(Dictionary<string, object?> catalog)
    {
        var levels = new List<object?>();
        if (catalog.TryGetValue("supported_reasoning_levels", out var value)
            && value is IEnumerable<object?> values)
        {
            foreach (var item in values)
            {
                if (AsDictionary(item) is not { } level)
                {
                    continue;
                }

                var effort = ReadString(level, "effort");
                if (effort.Length == 0)
                {
                    continue;
                }

                var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in level)
                {
                    normalized[pair.Key] = pair.Value;
                }

                normalized["effort"] = effort;
                if (ReadString(normalized, "description").Length == 0)
                {
                    normalized["description"] = effort;
                }

                levels.Add(normalized);
            }
        }

        catalog["supported_reasoning_levels"] = levels.Count > 0
            ? levels
            : DefaultReasoningLevels();
    }

    private static void NormalizeDefaultReasoningLevel(Dictionary<string, object?> catalog)
    {
        var efforts = (catalog["supported_reasoning_levels"] as IEnumerable<object?> ?? [])
            .Select(AsDictionary)
            .Where(level => level is not null)
            .Select(level => ReadString(level!, "effort"))
            .Where(effort => effort.Length > 0)
            .ToList();

        var current = ReadString(catalog, "default_reasoning_level");
        if (efforts.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        catalog["default_reasoning_level"] = efforts.FirstOrDefault(effort =>
            effort.Equals("medium", StringComparison.OrdinalIgnoreCase))
            ?? efforts.FirstOrDefault()
            ?? "medium";
    }

    private static void NormalizeDefaultReasoningSummary(Dictionary<string, object?> catalog)
    {
        var current = ReadString(catalog, "default_reasoning_summary");
        catalog["default_reasoning_summary"] = current is "auto" or "concise" or "detailed" or "none"
            ? current
            : "auto";
    }

    private static void NormalizeServiceTiers(Dictionary<string, object?> catalog)
    {
        if (catalog.TryGetValue("service_tiers", out var value)
            && value is IEnumerable<object?> values)
        {
            catalog["service_tiers"] = values.ToList();
            return;
        }

        catalog["service_tiers"] = DefaultServiceTiers();
    }

    private static void NormalizeAdditionalSpeedTiers(Dictionary<string, object?> catalog)
    {
        if (catalog.TryGetValue("additional_speed_tiers", out var value)
            && value is IEnumerable<object?> values)
        {
            catalog["additional_speed_tiers"] = values
                .Select(ReadString)
                .Where(speedTier => speedTier.Length > 0)
                .Cast<object?>()
                .ToList();
            return;
        }

        catalog["additional_speed_tiers"] = new List<object?> { "fast" };
    }

    private static void NormalizeTruncationPolicy(
        Dictionary<string, object?> catalog,
        long contextWindow)
    {
        if (catalog.TryGetValue("truncation_policy", out var value)
            && AsDictionary(value) is { } policy)
        {
            policy["mode"] = ReadString(policy, "mode") is { Length: > 0 } mode
                ? mode
                : "tokens";
            if (ReadPositiveLong(policy, "limit") is not > 0)
            {
                policy["limit"] = contextWindow;
            }

            catalog["truncation_policy"] = policy;
            return;
        }

        catalog["truncation_policy"] = new Dictionary<string, object?>
        {
            ["mode"] = "tokens",
            ["limit"] = contextWindow
        };
    }

    private static void EnsureInstructions(Dictionary<string, object?> catalog)
    {
        var hasBaseInstructions = ReadString(catalog, "base_instructions").Length > 0;
        var hasModelMessages = TryReadModelMessages(catalog, out var modelMessages)
            && ReadString(modelMessages, "instructions_template").Length > 0;

        if (!hasBaseInstructions)
        {
            catalog["base_instructions"] = CodexModelInstructions.BaseInstructions;
        }

        if (!hasModelMessages)
        {
            catalog["model_messages"] = CodexModelInstructions.ModelMessages;
        }
    }

    private static bool TryReadModelMessages(
        Dictionary<string, object?> catalog,
        out Dictionary<string, object?> modelMessages)
    {
        modelMessages = [];
        if (!catalog.TryGetValue("model_messages", out var value)
            || AsDictionary(value) is not { } messages)
        {
            return false;
        }

        modelMessages = messages;
        return true;
    }

    private static void EnsureBoolean(
        Dictionary<string, object?> catalog,
        string key,
        bool fallback)
    {
        if (TryReadBoolean(catalog, key, out var value))
        {
            catalog[key] = value;
            return;
        }

        catalog[key] = fallback;
    }

    private static void EnsureString(
        Dictionary<string, object?> catalog,
        string key,
        string fallback)
    {
        if (ReadString(catalog, key).Length > 0)
        {
            return;
        }

        catalog[key] = fallback;
    }

    private static void EnsureInteger(
        Dictionary<string, object?> catalog,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = ReadLong(catalog, key);
        if (value is null || value < minimum || value > maximum)
        {
            catalog[key] = fallback;
            return;
        }

        catalog[key] = value.Value;
    }

    private static void EnsurePositiveLong(
        Dictionary<string, object?> catalog,
        string key,
        long fallback)
    {
        if (ReadPositiveLong(catalog, key) is not > 0)
        {
            catalog[key] = fallback;
        }
    }

    private static Dictionary<string, object?>? AsDictionary(object? value)
    {
        if (value is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return readOnlyDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => JsonValueToObject(property.Value),
                StringComparer.Ordinal);
        }

        return null;
    }

    private static bool? ReadBoolean(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        return source.TryGetValue(key, out var value)
            ? ReadBoolean(value)
            : null;
    }

    private static bool? ReadBoolean(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null
        };
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, object?> source,
        string key,
        out bool value)
    {
        var parsed = ReadBoolean(source, key);
        value = parsed ?? false;
        return parsed.HasValue;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        return source.TryGetValue(key, out var value)
            ? ReadString(value)
            : string.Empty;
    }

    private static string ReadString(object? value)
    {
        return value switch
        {
            string text => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static long? ReadPositiveLong(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        var value = ReadLong(source, key);
        return value is > 0 ? value : null;
    }

    private static long? ReadLong(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        return source.TryGetValue(key, out var value)
            ? ReadLong(value)
            : null;
    }

    private static long? ReadLong(object? value)
    {
        return value switch
        {
            int integer => integer,
            long longValue => longValue,
            double fraction => (long)fraction,
            decimal decimalValue => (long)decimalValue,
            string text when long.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed) => parsed,
            _ => null
        };
    }

    private static object? JsonValueToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => JsonValueToObject(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonValueToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static List<object?> DefaultServiceTiers()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["id"] = "priority",
                ["name"] = "Fast",
                ["description"] = "1.5x speed, increased usage"
            }
        ];
    }

    private static List<object?> DefaultReasoningLevels()
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["effort"] = "low",
                ["description"] = "Quick responses with lighter reasoning"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "medium",
                ["description"] = "Balances speed and reasoning depth for everyday tasks"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "high",
                ["description"] = "Greater reasoning depth for complex problems"
            },
            new Dictionary<string, object?>
            {
                ["effort"] = "xhigh",
                ["description"] = "Extra high reasoning depth for extremely complex logic"
            }
        ];
    }
}
