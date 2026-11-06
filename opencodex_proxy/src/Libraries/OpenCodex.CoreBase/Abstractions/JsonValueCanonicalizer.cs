using System.Text;
using System.Text.Json;

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>Produces stable JSON text for structural equality checks.</summary>
public static class JsonValueCanonicalizer
{
    public static string Canonicalize(object? value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, json.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }
}
