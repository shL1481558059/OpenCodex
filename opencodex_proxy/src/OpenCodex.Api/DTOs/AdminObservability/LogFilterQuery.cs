namespace OpenCodex.Api.DTOs.AdminObservability;

public static class LogFilterQuery
{
    private static readonly string[] FilterKeys =
    [
        "request_id",
        "model",
        "upstream_model",
        "channel_id",
        "owner_username",
        "api_key_id",
        "path",
        "status_code",
        "is_stream",
        "client_ip",
        "error",
        "request_status",
        "created_from",
        "created_to"
    ];

    public static Dictionary<string, object?> FromQuery(
        IEnumerable<KeyValuePair<string, string?>> query,
        string? excludedKey = null)
    {
        var queryValues = query
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.Ordinal);
        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in FilterKeys)
        {
            if (key == excludedKey)
            {
                continue;
            }

            if (!queryValues.TryGetValue(key, out var value))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(value))
            {
                filters[key] = value;
            }
        }

        return filters;
    }
}
