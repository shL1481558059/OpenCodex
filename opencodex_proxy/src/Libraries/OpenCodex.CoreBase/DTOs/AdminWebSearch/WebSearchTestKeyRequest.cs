using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.AdminWebSearch;

public sealed class WebSearchTestKeyRequest
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    public string EffectiveQuery()
    {
        var query = Query.Trim();
        return query.Length == 0 ? "OpenAI" : query;
    }
}
