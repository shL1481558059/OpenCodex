namespace OpenCodex.Api.DTOs.AdminWebSearch;

public sealed class WebSearchTestKeyRequest
{
    public WebSearchTestKeyRequest(long? keyId, string query)
    {
        KeyId = keyId;
        Query = query;
    }

    public long? KeyId { get; }

    public string Query { get; }

    public static WebSearchTestKeyRequest From(IReadOnlyDictionary<string, object?> body)
    {
        var query = StringValue(body, "query");
        return new WebSearchTestKeyRequest(
            long.TryParse(StringValue(body, "id"), out var keyId) ? keyId : null,
            query.Length == 0 ? "OpenAI" : query);
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return (GetValue(dictionary, key)?.ToString() ?? string.Empty).Trim();
    }
}
