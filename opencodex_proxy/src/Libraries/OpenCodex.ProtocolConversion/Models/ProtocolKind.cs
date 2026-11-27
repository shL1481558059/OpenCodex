namespace OpenCodex.ProtocolConversion.Models;

public enum ProtocolKind
{
    Responses,
    Chat,
    Messages
}

public static class ProtocolKinds
{
    public static string ToWireValue(this ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.Responses => "responses",
        ProtocolKind.Chat => "chat",
        ProtocolKind.Messages => "messages",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    public static ProtocolKind Parse(string value) => value switch
    {
        "responses" => ProtocolKind.Responses,
        "chat" => ProtocolKind.Chat,
        "messages" => ProtocolKind.Messages,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported protocol kind.")
    };
}
