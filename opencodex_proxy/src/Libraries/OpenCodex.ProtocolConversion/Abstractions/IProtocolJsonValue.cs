using System.Text.Json;

namespace OpenCodex.ProtocolConversion.Abstractions;

/// <summary>A complete JSON value written in bounded segments by the protocol serializer.
/// Implementations must support repeated writes of the same immutable value.</summary>
public interface IProtocolJsonValue
{
    void Write(Utf8JsonWriter writer, CancellationToken cancellationToken);
}
