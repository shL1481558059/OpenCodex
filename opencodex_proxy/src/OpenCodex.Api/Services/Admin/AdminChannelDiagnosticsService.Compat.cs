using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Errors;

namespace OpenCodex.Api.Services;

public sealed partial class AdminChannelDiagnosticsService
{
    private static (Dictionary<string, object?> Payload, List<string> Details) ApplyCompat(
        IReadOnlyDictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> compat)
    {
        var result = CloneObject(payload);
        var details = new List<string>();

        foreach (var (key, value) in JsonDictionaryValue.Object(compat, "default_params", CloneObject))
        {
            if (!result.ContainsKey(key))
            {
                result[key] = CloneJsonValue(value);
                details.Add($"default:{key}");
            }
        }

        foreach (var (source, targetValue) in JsonDictionaryValue.Object(compat, "rename_params", CloneObject))
        {
            var target = targetValue?.ToString() ?? string.Empty;
            if (target.Length == 0 || !result.ContainsKey(source))
            {
                continue;
            }

            if (!result.ContainsKey(target))
            {
                result[target] = CloneJsonValue(result[source]);
            }

            result.Remove(source);
            details.Add($"rename:{source}->{target}");
        }

        foreach (var item in JsonDictionaryValue.List(compat, "drop_params"))
        {
            var key = item?.ToString() ?? string.Empty;
            if (key.Length > 0 && result.Remove(key))
            {
                details.Add($"drop:{key}");
            }
        }

        foreach (var (key, value) in JsonDictionaryValue.Object(compat, "force_params", CloneObject))
        {
            result[key] = CloneJsonValue(value);
            details.Add($"force:{key}");
        }

        var unsupported = JsonDictionaryValue.List(compat, "unsupported_params")
            .Select(item => item?.ToString() ?? string.Empty)
            .Where(key => key.Length > 0 && result.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new BadRequestException($"upstream does not support parameter(s): {string.Join(", ", unsupported)}");
        }

        return (result, details);
    }
}
