using System.Text.Json;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    /// <summary>Checks executable response items against the exact tools declared by this request.</summary>
    public static void ValidateResponsesToolCalls(Dictionary<string, object?> request, Dictionary<string, object?> response)
    {
        var declared = new Dictionary<(string Namespace, string Name), (string Type, string? Execution)>();
        RegisterResponseToolDeclarations(GetValue(request, "tools"), string.Empty, declared);
        RegisterResponseToolDeclarations(GetValue(request, "additional_tools"), string.Empty, declared);
        foreach (var value in ListValue(request, "input"))
        {
            if (!TryAsObject(value, out var item)) continue;
            if (GetString(item, "type") is "additional_tools" or "tool_search_output")
                RegisterResponseToolDeclarations(GetValue(item, "tools"), string.Empty, declared);
            RegisterResponseToolDeclarations(GetValue(item, "additional_tools"), string.Empty, declared);
        }

        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in ListValue(response, "output"))
        {
            if (!TryAsObject(value, out var item)) continue;
            var type = GetString(item, "type");
            if (type is not ("function_call" or "custom_tool_call" or "tool_search_call")) continue;

            if (GetValue(item, "name") is not string name || string.IsNullOrEmpty(name)
                || GetValue(item, "namespace") is not (null or string))
                throw InvalidResponseToolContract();
            var ns = GetString(item, "namespace") ?? string.Empty;
            if (!declared.TryGetValue((ns, name), out var declaration) || declaration.Type != type)
                throw InvalidResponseToolContract();
            if (GetValue(item, "call_id") is not string callId || string.IsNullOrEmpty(callId) || !callIds.Add(callId))
                throw InvalidResponseToolContract();

            if (type == "tool_search_call")
            {
                if (GetString(item, "execution") != declaration.Execution
                    || GetValue(item, "arguments") is not IReadOnlyDictionary<string, object?> || item.ContainsKey("input"))
                    throw InvalidResponseToolContract();
                continue;
            }

            var field = type == "custom_tool_call" ? "input" : "arguments";
            if (GetValue(item, field) is not string payload || item.ContainsKey(field == "input" ? "arguments" : "input"))
                throw InvalidResponseToolContract();
            if (type == "function_call")
            {
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateJsonProperties(document.RootElement))
                        throw InvalidResponseToolContract();
                }
                catch (JsonException)
                {
                    throw InvalidResponseToolContract();
                }
            }
        }
    }

    private static void RegisterResponseToolDeclarations(object? values, string ns,
        Dictionary<(string Namespace, string Name), (string Type, string? Execution)> declared)
    {
        foreach (var value in AsOptionalList(values))
        {
            if (!TryAsObject(value, out var tool)) continue;
            var name = GetString(tool, "name") ?? string.Empty;
            var type = GetString(tool, "type") ?? "function";
            if (type == "tool_search" && name.Length == 0) name = "tool_search";
            if (type == "namespace")
            {
                RegisterResponseToolDeclarations(GetValue(tool, "tools"),
                    string.IsNullOrEmpty(ns) ? name : $"{ns}{NamespaceSeparator}{name}", declared);
                continue;
            }
            if (name.Length == 0 || type is not ("function" or "custom" or "custom_tool" or "tool_search")) continue;
            var callType = type == "function" ? "function_call" : type == "tool_search" ? "tool_search_call" : "custom_tool_call";
            var execution = type == "tool_search" ? GetString(tool, "execution") ?? "client" : null;
            var declaration = (callType, execution);
            if (declared.TryGetValue((ns, name), out var existing) && existing != declaration)
                throw new BadRequestException("A tool identity has conflicting type or execution declarations.");
            declared[(ns, name)] = declaration;
        }
    }

    private static bool HasDuplicateJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateJsonProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (HasDuplicateJsonProperties(item)) return true;
        }
        return false;
    }

    private static UpstreamException InvalidResponseToolContract() => new(
        "Upstream tool call violates the declared tool identity, type, or payload contract. Retry with a valid declared tool call.");
}
