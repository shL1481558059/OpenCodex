using System.Text.Json;

namespace OpenCodex.Api.Protocols;

public static partial class ProtocolConverter
{
    private static List<object?> ExpandApplyPatchProxyTools(List<object?> tools)
    {
        var result = new List<object?>();
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            if (IsApplyPatchCanonicalTool(tool))
            {
                result.AddRange(ApplyPatchProxyTools(tool));
            }
            else
            {
                result.Add(tool);
            }
        }

        return DedupeCanonicalTools(result);
    }

    private static bool IsApplyPatchCanonicalTool(Dictionary<string, object?> tool)
    {
        var nativeType = GetString(tool, "native_type") ?? "function";
        var name = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
        if (nativeType == "apply_patch")
        {
            return true;
        }

        if (IsApplyPatchName(name) && nativeType is "custom" or "apply_patch")
        {
            return true;
        }

        return TryAsObject(GetValue(tool, "raw"), out var raw)
            && GetString(raw, "type") == "custom"
            && IsApplyPatchName(name);
    }

    private static List<object?> ApplyPatchProxyTools(Dictionary<string, object?> tool)
    {
        var description = Convert.ToString(GetValue(tool, "description"));
        if (string.IsNullOrEmpty(description))
        {
            description = "Apply a patch.";
        }

        return
        [
            Obj(
                ("name", "apply_patch_add_file"),
                ("description", $"{description} Create one new file with structured JSON."),
                ("parameters", ApplyPatchSingleOpSchema("add_file")),
                ("native_type", "function")),
            Obj(
                ("name", "apply_patch_delete_file"),
                ("description", $"{description} Delete one file with structured JSON."),
                ("parameters", ApplyPatchSingleOpSchema("delete_file")),
                ("native_type", "function")),
            Obj(
                ("name", "apply_patch_update_file"),
                ("description", $"{description} Edit one existing file with structured hunks."),
                ("parameters", ApplyPatchSingleOpSchema("update_file")),
                ("native_type", "function")),
            Obj(
                ("name", "apply_patch_replace_file"),
                ("description", $"{description} Replace one file entirely with structured JSON."),
                ("parameters", ApplyPatchSingleOpSchema("replace_file")),
                ("native_type", "function")),
            Obj(
                ("name", "apply_patch_batch"),
                ("description", $"{description} Apply multiple structured patch operations."),
                ("parameters", ApplyPatchBatchSchema()),
                ("native_type", "function"))
        ];
    }

    private static Dictionary<string, object?> ApplyPatchSingleOpSchema(string action)
    {
        var properties = Obj(("path", Obj(("type", "string"), ("description", "Target file path."))));
        var required = new List<object?> { "path" };
        if (action is "add_file" or "replace_file")
        {
            properties["content"] = Obj(("type", "string"), ("description", "Full file content."));
            required.Add("content");
        }
        else if (action == "update_file")
        {
            properties["move_to"] = Obj(("type", "string"), ("description", "Optional destination path for a file move."));
            properties["hunks"] = ApplyPatchHunksSchema();
            required.Add("hunks");
        }

        return Obj(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", properties),
            ("required", required));
    }

    private static Dictionary<string, object?> ApplyPatchBatchSchema()
    {
        var operationProperties = Obj(
            ("type", Obj(("type", "string"), ("enum", new List<object?> { "add_file", "delete_file", "update_file", "replace_file" }))),
            ("path", Obj(("type", "string"), ("description", "Target file path."))),
            ("move_to", Obj(("type", "string"), ("description", "Optional destination path for a file move."))),
            ("content", Obj(("type", "string"), ("description", "File content."))),
            ("hunks", ApplyPatchHunksSchema()));
        var operationItem = Obj(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", operationProperties),
            ("required", new List<object?> { "type", "path" }));
        var operations = Obj(
            ("type", "array"),
            ("description", "Structured patch operations."),
            ("minItems", 1),
            ("items", operationItem));

        return Obj(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", Obj(("operations", operations))),
            ("required", new List<object?> { "operations" }));
    }

    private static Dictionary<string, object?> ApplyPatchHunksSchema()
    {
        var lineItem = Obj(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", Obj(
                ("op", Obj(("type", "string"), ("enum", new List<object?> { "context", "add", "remove", "eof" }))),
                ("text", Obj(("type", "string"))))),
            ("required", new List<object?> { "op", "text" }));
        var lines = Obj(
            ("type", "array"),
            ("items", lineItem));
        var hunkItem = Obj(
            ("type", "object"),
            ("additionalProperties", false),
            ("properties", Obj(("lines", lines))),
            ("required", new List<object?> { "lines" }));

        return Obj(
            ("type", "array"),
            ("description", "Structured update hunks."),
            ("minItems", 1),
            ("items", hunkItem));
    }

    private static object? NormalizeApplyPatchArguments(string itemType, string name, object? arguments)
    {
        var normalizedName = name.Replace("-", "_", StringComparison.Ordinal);
        if (!IsApplyPatchName(normalizedName) && itemType != "apply_patch_call")
        {
            return arguments;
        }

        if (arguments is string text)
        {
            return IsJsonObjectString(text) ? text : Obj(("patch", text));
        }

        if (TryAsObject(arguments, out var dictionary))
        {
            if (dictionary.ContainsKey("patch"))
            {
                return dictionary;
            }

            if (dictionary.Count == 1 && dictionary.ContainsKey("input"))
            {
                return Obj(("patch", dictionary["input"]));
            }
        }

        return arguments;
    }

    private static bool IsJsonObjectString(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
