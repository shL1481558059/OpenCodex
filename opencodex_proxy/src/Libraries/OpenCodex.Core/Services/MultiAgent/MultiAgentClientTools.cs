using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Preserves the client's native collaboration contract without inventing tool capabilities.</summary>
public sealed class MultiAgentClientTools
{
    private static readonly HashSet<string> Actions =
        ["spawn_agent", "send_message", "followup_task", "wait_agent", "interrupt_agent", "list_agents"];
    private readonly Dictionary<(string Namespace, string Name), Dictionary<string, object?>> _tools = [];

    private MultiAgentClientTools(List<object?> definitions)
    {
        Definitions = definitions;
        foreach (var definition in definitions.OfType<Dictionary<string, object?>>())
        {
            if (JsonDictionaryValue.String(definition, "type") == "namespace")
            {
                foreach (var tool in JsonDictionaryValue.List(definition, "tools").OfType<Dictionary<string, object?>>())
                    Register("collaboration", tool);
            }
            else Register("", definition);
        }
    }

    public bool CanSpawn => _tools.Keys.Any(key => ActionName(key.Namespace, key.Name) == "spawn_agent");
    public List<object?> Definitions { get; }

    public static MultiAgentClientTools FromRequest(Dictionary<string, object?> request) => FromDefinitions(
        JsonDictionaryValue.List(request, "tools")
            .Concat(JsonDictionaryValue.List(request, "additional_tools"))
            .Concat(JsonDictionaryValue.List(request, "input").OfType<IReadOnlyDictionary<string, object?>>()
                .SelectMany(item => JsonDictionaryValue.String(item, "type") == "additional_tools"
                    ? JsonDictionaryValue.List(item, "tools") : JsonDictionaryValue.List(item, "additional_tools"))));

    public static MultiAgentClientTools FromDefinitions(IEnumerable<object?> definitions)
    {
        var selected = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var value in definitions)
        {
            if (value is not IReadOnlyDictionary<string, object?> definition) continue;
            var name = JsonDictionaryValue.String(definition, "name");
            var isNamespace = JsonDictionaryValue.String(definition, "type") == "namespace";
            if (isNamespace ? name != "collaboration" : ActionName("", name) is null) continue;
            var copy = WebSearchPayload.DeepCopyObject(definition);
            if (isNamespace && selected.TryGetValue(name, out var previous))
            {
                var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tool in JsonDictionaryValue.List(previous, "tools").Concat(JsonDictionaryValue.List(copy, "tools")))
                {
                    if (tool is not IReadOnlyDictionary<string, object?> child)
                        throw new BadRequestException("Client collaboration tools must be objects.");
                    merged[JsonDictionaryValue.String(child, "name")] = WebSearchPayload.DeepCopyObject(child);
                }
                copy["tools"] = merged.Values.ToList();
            }
            selected[name] = copy;
        }
        return new(selected.Values.Cast<object?>().ToList());
    }

    public string? Action(Dictionary<string, object?> call)
    {
        var key = CallKey(call);
        return _tools.ContainsKey(key) ? ActionName(key.Namespace, key.Name) : null;
    }

    public Dictionary<string, object?> Arguments(Dictionary<string, object?> call)
    {
        if (!_tools.TryGetValue(CallKey(call), out var tool))
            throw new BadRequestException("Client collaboration tool was not declared in this request.");
        var action = Action(call)!;
        Dictionary<string, object?> arguments;
        var supplied = JsonDictionaryValue.Get(call, "arguments");
        if (supplied is string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new BadRequestException($"Client {action} arguments must be a JSON object.");
                arguments = (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)!;
            }
            catch (JsonException)
            {
                throw new BadRequestException($"Client {action} arguments are invalid JSON.");
            }
            catch (ArgumentException)
            {
                throw new BadRequestException($"Client {action} arguments contain duplicate fields.");
            }
        }
        else if (supplied is IReadOnlyDictionary<string, object?> structured)
            arguments = WebSearchPayload.DeepCopyObject(structured);
        else throw new BadRequestException($"Client {action} arguments must be a JSON object.");

        var schema = JsonDictionaryValue.Object(tool, "parameters", WebSearchPayload.DeepCopyObject);
        var properties = JsonDictionaryValue.Object(schema, "properties", WebSearchPayload.DeepCopyObject);
        foreach (var required in JsonDictionaryValue.List(schema, "required").OfType<string>())
            if (!arguments.ContainsKey(required))
                throw new BadRequestException($"Client {action} is missing a required argument: {required}.");
        foreach (var (name, value) in arguments)
        {
            if (!properties.TryGetValue(name, out var property) || property is not IReadOnlyDictionary<string, object?> definition)
                throw new BadRequestException($"Client {action} contains an undeclared argument.");
            ValidateValue(action, name, value, definition);
        }
        return arguments;
    }

    public Dictionary<string, object?> ApplyToTemplate(Dictionary<string, object?> normalizedTemplate)
    {
        var result = WebSearchPayload.DeepCopyObject(normalizedTemplate);
        result["tools"] = JsonDictionaryValue.List(result, "tools")
            .Where(tool => tool is not IReadOnlyDictionary<string, object?> definition
                || !IsCollaborationOrInternal(definition))
            .Concat(Definitions.Select(WebSearchPayload.DeepCopy)).ToList();
        return result;
    }

    private void Register(string ns, Dictionary<string, object?> tool)
    {
        var name = JsonDictionaryValue.String(tool, "name");
        if (ActionName(ns, name) is null) return;
        if (JsonDictionaryValue.String(tool, "type") != "function"
            || JsonDictionaryValue.Get(tool, "parameters") is not IReadOnlyDictionary<string, object?>)
            throw new BadRequestException("Client collaboration actions require function parameter schemas.");
        _tools[(ns, name)] = tool;
    }

    private static bool IsCollaborationOrInternal(IReadOnlyDictionary<string, object?> definition)
    {
        var name = JsonDictionaryValue.String(definition, "name");
        return name.StartsWith("ocxp_ma_", StringComparison.Ordinal) || name == "collaboration"
            || ActionName("", name) is not null;
    }

    private static (string Namespace, string Name) CallKey(Dictionary<string, object?> call) =>
        (JsonDictionaryValue.String(call, "namespace"), JsonDictionaryValue.String(call, "name"));

    private static string? ActionName(string ns, string name)
    {
        if (ns == "collaboration") return Actions.Contains(name) ? name : null;
        if (ns.Length > 0) return null;
        foreach (var prefix in new[] { "collaboration.", "collaboration__", "collaboration_" })
            if (name.StartsWith(prefix, StringComparison.Ordinal) && Actions.Contains(name[prefix.Length..]))
                return name[prefix.Length..];
        return null;
    }

    private static void ValidateValue(string action, string name, object? value, IReadOnlyDictionary<string, object?> schema)
    {
        using var document = JsonDocument.Parse(WebSearchPayload.JsonDumps(value));
        var element = document.RootElement;
        var types = JsonDictionaryValue.Get(schema, "type") is string type
            ? new[] { type } : JsonDictionaryValue.List(schema, "type").OfType<string>().ToArray();
        if (types.Length > 0 && !types.Any(candidate => MatchesType(element, candidate)))
            throw new BadRequestException($"Client {action} argument {name} has an invalid type.");
        if (schema.ContainsKey("enum") && !JsonDictionaryValue.List(schema, "enum")
                .Any(candidate => JsonElement.DeepEquals(element, JsonSerializer.SerializeToElement(candidate))))
            throw new BadRequestException($"Client {action} argument {name} is outside its declared enum.");
    }

    private static bool MatchesType(JsonElement element, string type) => type switch
    {
        "string" => element.ValueKind == JsonValueKind.String,
        "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number) && number == decimal.Truncate(number),
        "number" => element.ValueKind == JsonValueKind.Number,
        "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => element.ValueKind == JsonValueKind.Object,
        "array" => element.ValueKind == JsonValueKind.Array,
        "null" => element.ValueKind == JsonValueKind.Null,
        _ => false
    };
}
