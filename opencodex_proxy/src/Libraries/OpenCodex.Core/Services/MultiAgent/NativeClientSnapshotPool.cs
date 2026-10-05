using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>
/// Owns private immutable JSON snapshots for one owner and thread. Input and output lists
/// remain independent, while repeated history items and tool declarations share their content.
/// Callers must deep-copy a snapshot before returning it outside the session store.
/// </summary>
internal sealed class NativeClientSnapshotPool
{
    private readonly Dictionary<string, object?> _snapshots = new(StringComparer.Ordinal);

    public D Snapshot(D value)
    {
        var result = new D();
        foreach (var (name, content) in value)
            result[name] = name is "input" or "output" && content is List<object?> items
                ? items.Select(Intern).ToList() : Intern(content);
        return result;
    }

    private object? Intern(object? value)
    {
        if (value is null || value.GetType().IsValueType) return value;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value))));
        if (_snapshots.TryGetValue(hash, out var snapshot)) return snapshot;
        snapshot = WebSearchPayload.DeepCopy(value);
        _snapshots.Add(hash, snapshot);
        return snapshot;
    }

    internal static string Canonical(object? value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory)) WriteCanonical(writer, json.RootElement);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
