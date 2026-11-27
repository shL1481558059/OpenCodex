using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodex.ProtocolConversion.Models;

internal static class CanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, Options);

    public static object? ToObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined
            ? null
            : JsonSerializer.Deserialize<object?>(element, Options);

    public static JsonElement FromObject(object? value) =>
        value is null ? default : JsonSerializer.SerializeToElement(value, Options);
}
