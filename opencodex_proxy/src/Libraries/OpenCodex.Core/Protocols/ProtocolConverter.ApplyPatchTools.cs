using System.Text.Json;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    internal static bool IsApplyPatchToolName(string name)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal);
        return normalized == "apply_patch"
            || normalized.StartsWith("apply_patch_", StringComparison.Ordinal)
            || normalized.EndsWith("/apply_patch", StringComparison.Ordinal);
    }

    internal static Dictionary<string, object?> ResponsesToolCallItemFromToolCall(
        object? callId,
        object? name,
        object? arguments,
        object? namespaceValue = null,
        string? itemId = null)
    {
        var toolName = Convert.ToString(name) ?? string.Empty;

        var functionCall = Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", "function_call"),
            ("status", "completed"),
            ("call_id", callId),
            ("arguments", JsonDumps(arguments ?? "{}")));
        MergeInto(functionCall, ResponsesFunctionCallNameFields(toolName, namespaceValue));
        return functionCall;
    }

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

    private static Dictionary<string, object?> ResponsesApplyPatchItemFromToolCall(
        object? callId,
        string name,
        object? arguments,
        string? itemId)
    {
        var patch = ApplyPatchInputFromToolCall(name, arguments);
        return Obj(
            ("id", itemId ?? NewId("fc")),
            ("type", "function_call"),
            ("status", "completed"),
            ("call_id", callId),
            ("name", "exec_command"),
            ("arguments", JsonDumps(Obj(("cmd", ApplyPatchExecCommand(patch))))));
    }

    private static string ApplyPatchInputFromToolCall(string name, object? arguments)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal);
        if (normalized.StartsWith("apply_patch_", StringComparison.Ordinal) && normalized != "apply_patch")
        {
            var rebuilt = RebuildApplyPatchGrammar(normalized, arguments);
            if (rebuilt is not null)
            {
                return rebuilt;
            }
        }

        return ApplyPatchInputFromArguments(arguments);
    }

    private static string ApplyPatchInputFromArguments(object? arguments)
    {
        var value = DecodeJsonValue(arguments);
        var patch = ExtractPatchValue(value);
        if (patch is null)
        {
            return arguments is string text ? text : JsonDumps(arguments);
        }

        return patch is string patchText ? patchText : JsonDumps(patch);
    }

    private static string? RebuildApplyPatchGrammar(string name, object? arguments)
    {
        var value = DecodeJsonValue(arguments);
        if (!TryAsObject(value, out var dictionary))
        {
            return null;
        }

        var action = name["apply_patch_".Length..];
        List<Dictionary<string, object?>> operations;
        if (action == "batch")
        {
            operations = ApplyPatchBatchOperations(GetValue(dictionary, "operations"));
            if (operations.Count == 0)
            {
                return null;
            }
        }
        else
        {
            var operation = new Dictionary<string, object?>(dictionary, StringComparer.Ordinal)
            {
                ["type"] = action
            };
            operations = [operation];
        }

        var body = new List<string>();
        foreach (var operation in operations)
        {
            body.AddRange(ApplyPatchOperationLines(operation));
        }

        return body.Count == 0
            ? null
            : string.Join('\n', new[] { "*** Begin Patch" }.Concat(body).Append("*** End Patch"));
    }

    private static List<Dictionary<string, object?>> ApplyPatchBatchOperations(object? value)
    {
        value = DecodeJsonValue(value);
        if (!TryAsList(value, out var list))
        {
            return [];
        }

        return list
            .Where(item => TryAsObject(item, out _))
            .Select(AsObject)
            .ToList();
    }

    private static List<string> ApplyPatchOperationLines(Dictionary<string, object?> operation)
    {
        var opType = Convert.ToString(GetValue(operation, "type")) ?? string.Empty;
        var path = ApplyPatchPath(GetValue(operation, "path"));
        if (path.Length == 0)
        {
            return [];
        }

        if (opType == "add_file")
        {
            return [$"*** Add File: {path}", ..PrefixedContentLines(GetValue(operation, "content"), "+")];
        }

        if (opType == "delete_file")
        {
            return [$"*** Delete File: {path}"];
        }

        if (opType == "replace_file")
        {
            return
            [
                $"*** Delete File: {path}",
                $"*** Add File: {path}",
                ..PrefixedContentLines(GetValue(operation, "content"), "+")
            ];
        }

        if (opType != "update_file")
        {
            return [];
        }

        var lines = new List<string> { $"*** Update File: {path}" };
        var rawMoveTo = GetValue(operation, "move_to");
        if (!string.IsNullOrWhiteSpace(Convert.ToString(rawMoveTo)))
        {
            var moveTo = ApplyPatchPath(rawMoveTo);
            if (moveTo.Length == 0)
            {
                return [];
            }

            lines.Add($"*** Move to: {moveTo}");
        }

        if (!TryAsList(GetValue(operation, "hunks"), out var hunks))
        {
            return [];
        }

        foreach (var hunkItem in hunks)
        {
            if (!TryAsObject(hunkItem, out var hunk))
            {
                continue;
            }

            lines.Add("@@");
            if (!TryAsList(GetValue(hunk, "lines"), out var hunkLines))
            {
                continue;
            }

            foreach (var lineItem in hunkLines)
            {
                if (!TryAsObject(lineItem, out var line))
                {
                    continue;
                }

                var text = Convert.ToString(GetValue(line, "text")) ?? string.Empty;
                switch (Convert.ToString(GetValue(line, "op")))
                {
                    case "add":
                        lines.Add($"+{text}");
                        break;
                    case "remove":
                        lines.Add($"-{text}");
                        break;
                    case "eof":
                        lines.Add("*** End of File");
                        break;
                    default:
                        lines.Add($" {text}");
                        break;
                }
            }
        }

        return lines;
    }

    private static string ApplyPatchPath(object? value)
    {
        var path = (Convert.ToString(value) ?? string.Empty).Trim();
        return path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal)
            ? string.Empty
            : path;
    }

    private static List<string> PrefixedContentLines(object? content, string prefix)
    {
        return (Convert.ToString(content) ?? string.Empty)
            .Split('\n')
            .Select(line => $"{prefix}{line}")
            .ToList();
    }

    private static object? ExtractPatchValue(object? value)
    {
        if (TryAsObject(value, out var dictionary))
        {
            foreach (var key in new[] { "patch", "input" })
            {
                if (dictionary.ContainsKey(key))
                {
                    return dictionary[key];
                }
            }

            if (dictionary.ContainsKey("command"))
            {
                return ExtractPatchCommand(dictionary["command"]);
            }
        }

        if (TryAsList(value, out var list))
        {
            return ExtractPatchCommand(list);
        }

        return null;
    }

    private static object? ExtractPatchCommand(object? command)
    {
        command = DecodeJsonValue(command);
        if (TryAsObject(command, out var dictionary))
        {
            return ExtractPatchValue(dictionary);
        }

        if (TryAsList(command, out var list))
        {
            if (list.Count >= 2 && IsApplyPatchExecutable(list[0]))
            {
                return ExtractPatchValue(list[1]) ?? list[1];
            }

            return list.FirstOrDefault(item => item is string text && LooksLikePatch(text));
        }

        return command is string text && LooksLikePatch(text) ? text : null;
    }

    private static string ApplyPatchExecCommand(string patch)
    {
        const string baseDelimiter = "OPENCODEX_PATCH";
        var delimiter = baseDelimiter;
        while (patch.Split('\n').Any(line => line.TrimEnd('\r') == delimiter))
        {
            delimiter += "_END";
        }

        var suffix = patch.EndsWith('\n') ? string.Empty : "\n";
        return $"apply_patch <<'{delimiter}'\n{patch}{suffix}{delimiter}";
    }

    private static object? DecodeJsonValue(object? value)
    {
        if (value is not string text)
        {
            return value;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool IsApplyPatchExecutable(object? value)
    {
        var executable = (Convert.ToString(value) ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
        return IsApplyPatchName(executable);
    }

    private static bool LooksLikePatch(string value)
    {
        return value.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal);
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
