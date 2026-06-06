using System.Text.Json.Serialization;

namespace OpenCodex.Api.DTOs.Admin;

public sealed class AdminErrorResponse
{
    public AdminErrorResponse(string error)
    {
        Error = error;
    }

    [JsonPropertyName("error")]
    public string Error { get; }
}
