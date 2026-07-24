namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    internal static IReadOnlyDictionary<string, ResponsesToolCallMapping> BuildResponsesToolCallMappings(
        Dictionary<string, object?> payload)
    {
        var result = new Dictionary<string, ResponsesToolCallMapping>(StringComparer.Ordinal);
        foreach (var item in ResponsesRequestToolsToCanonical(payload))
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            // Native remote MCP tools execute on the Responses provider. They are not
            // client-side function calls and must never participate in the legacy
            // namespace/function call restoration map.
            if (IsNativeRemoteMcpCanonicalTool(tool))
            {
                continue;
            }

            var nativeType = (GetString(tool, "native_type") ?? "function")
                .Replace("-", "_", StringComparison.Ordinal);
            if (nativeType == "function")
            {
                continue;
            }

            var responsesName = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
            if (responsesName.Length == 0)
            {
                continue;
            }

            var chatName = NamespaceNameToChat(responsesName);
            result[chatName] = new ResponsesToolCallMapping
            {
                ChatName = chatName,
                NativeType = nativeType,
                ResponsesName = responsesName,
                Namespace = GetString(tool, "namespace")
            };
        }

        return result;
    }

    private static List<object?> ResponsesToolsToCanonical(object? tools, IReadOnlyDictionary<string, object?>? compat = null)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            result.AddRange(ResponsesToolToCanonicalItems(item, compat));
        }

        return DedupeCanonicalTools(result);
    }

    private static List<object?> ResponsesRequestToolsToCanonical(
        Dictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?>? compat = null)
    {
        var result = ResponsesToolsToCanonical(GetValue(payload, "tools"), compat);
        if (!TryAsList(GetValue(payload, "input"), out var inputItems))
        {
            return result;
        }

        foreach (var item in inputItems)
        {
            if (!TryAsObject(item, out var inputItem))
            {
                continue;
            }

            var inputType = GetString(inputItem, "type");
            // Codex / Responses advanced tool loading: tools may appear mid-input instead of
            // only on the top-level tools array.
            if (inputType is not ("additional_tools" or "tool_search_output"))
            {
                continue;
            }

            result.AddRange(ResponsesToolsToCanonical(GetValue(inputItem, "tools"), compat));
        }

        return DedupeCanonicalTools(result);
    }

    private static List<object?> ChatToolsToCanonical(object? tools, IReadOnlyDictionary<string, object?>? compat = null)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var function = GetString(tool, "type") == "function" ? ObjectValue(tool, "function") : tool;
            var rawName = Convert.ToString(GetValue(function, "name")) ?? string.Empty;
            var normalizedName = rawName.Replace("-", "_", StringComparison.Ordinal);
            var nativeType = IsApplyPatchName(normalizedName) ? "apply_patch" : "function";
            var entry = Obj(
                ("name", rawName),
                ("description", GetValue(function, "description") ?? string.Empty),
                ("parameters", GetValue(function, "parameters") ?? new Dictionary<string, object?>()),
                ("native_type", nativeType));
            if (rawName.Length > 0)
            {
                if (rawName.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
                {
                    entry["name"] = NamespaceNameToChat(rawName);
                    entry["namespace"] = rawName[..rawName.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal)];
                }
                else
                {
                    var (namespaceName, _) = NamespaceCallParts(rawName);
                    if (!string.IsNullOrEmpty(namespaceName))
                    {
                        entry["namespace"] = namespaceName;
                    }
                }
            }

            if (nativeType == "apply_patch" && compat is not null)
            {
                entry["compat"] = new Dictionary<string, object?>(compat, StringComparer.Ordinal);
                entry = RewriteApplyPatchToolDescription(entry);
            }

            result.Add(entry);
        }

        return result;
    }

    private static List<object?> AnthropicToolsToCanonical(object? tools)
    {
        return AnthropicToolsToCanonical(tools, null);
    }

    internal static List<object?> AnthropicToolsToCanonical(object? tools, object? mcpServers)
    {
        var result = new List<object?>();
        foreach (var item in AsOptionalList(tools))
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            if (IsAnthropicMcpToolset(tool))
            {
                result.Add(AnthropicMcpToolsetToCanonical(tool));
                continue;
            }

            result.Add(Obj(
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("parameters", GetValue(tool, "input_schema") ?? new Dictionary<string, object?>()),
                ("native_type", "function")));
        }

        return EnrichCanonicalMcpToolsWithAnthropicServers(result, mcpServers);
    }

    private static List<object?> CanonicalToolsToResponses(List<object?> tools)
    {
        var result = new List<object?>();
        var namespaceGroups = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var nativeType = GetString(tool, "native_type") ?? "function";
            if (IsNativeRemoteMcpCanonicalTool(tool))
            {
                if (!TryCanonicalMcpToolToResponses(tool, out var responseMcpTool, out var error))
                {
                    ThrowRemoteMcpConversionError(tool, Responses, error);
                }

                result.Add(responseMcpTool);
                continue;
            }

            if (nativeType != "function" && TryAsObject(GetValue(tool, "raw"), out var raw) && raw.Count > 0)
            {
                result.Add(DeepCopy(raw));
                continue;
            }

            var namespaceName = GetString(tool, "namespace");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                var name = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
                var prefix = $"{namespaceName}{NamespaceSeparator}";
                var bareName = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
                if (bareName.Contains(LegacyNamespaceSeparator, StringComparison.Ordinal))
                {
                    bareName = bareName[(bareName.LastIndexOf(LegacyNamespaceSeparator, StringComparison.Ordinal) + 1)..];
                }

                if (!namespaceGroups.TryGetValue(namespaceName, out var group))
                {
                    group = [];
                    namespaceGroups[namespaceName] = group;
                }

                group.Add(Obj(
                    ("type", "function"),
                    ("name", bareName),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>())));
                continue;
            }

            result.Add(Obj(
                ("type", "function"),
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>())));
        }

        foreach (var (namespaceName, innerTools) in namespaceGroups)
        {
            result.Add(Obj(
                ("type", "namespace"),
                ("name", namespaceName),
                ("tools", innerTools)));
        }

        return result;
    }

    private static List<object?> CanonicalToolsToChat(List<object?> tools)
    {
        var result = new List<object?>();
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            if (IsNativeRemoteMcpCanonicalTool(tool))
            {
                ThrowRemoteMcpConversionError(
                    tool,
                    Chat,
                    "Chat Completions has no native remote MCP tool definition; use a Responses/Messages upstream or an explicit proxy-side MCP bridge");
            }

            if (!HasNonNullValue(tool, "name"))
            {
                continue;
            }

            tool = RewriteApplyPatchToolDescription(tool);

            result.Add(Obj(
                ("type", "function"),
                ("function", Obj(
                    ("name", NamespaceNameToChat(Convert.ToString(GetValue(tool, "name")) ?? string.Empty)),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", SanitizeToolSchema(GetValue(tool, "parameters") ?? new Dictionary<string, object?>()))))));
        }

        return result;
    }

    private static List<object?> CanonicalToolsToAnthropic(List<object?> tools)
    {
        var result = new List<object?>();
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            if (IsNativeRemoteMcpCanonicalTool(tool))
            {
                if (!TryCanonicalMcpToolToAnthropic(tool, out var anthropicMcpTool, out var error))
                {
                    ThrowRemoteMcpConversionError(tool, Messages, error);
                }

                result.Add(anthropicMcpTool);
                continue;
            }

            if (!HasNonNullValue(tool, "name"))
            {
                continue;
            }

            tool = RewriteApplyPatchToolDescription(tool);

            result.Add(Obj(
                ("name", GetValue(tool, "name")),
                ("description", GetValue(tool, "description") ?? string.Empty),
                ("input_schema", SanitizeToolSchema(GetValue(tool, "parameters") ?? new Dictionary<string, object?>()))));
        }

        return result;
    }

    private static List<object?> ResponsesToolToCanonicalItems(object? item, IReadOnlyDictionary<string, object?>? compat = null)
    {
        if (!TryAsObject(item, out var tool))
        {
            return [];
        }

        var toolType = GetString(tool, "type") ?? "function";
        var toolName = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
        if (toolType == "namespace")
        {
            var namespaceName = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
            var result = new List<object?>();
            foreach (var nested in ListValue(tool, "tools"))
            {
                foreach (var nestedItem in ResponsesToolToCanonicalItems(nested, compat))
                {
                    if (!TryAsObject(nestedItem, out var nestedTool))
                    {
                        continue;
                    }

                    var bareName = Convert.ToString(GetValue(nestedTool, "name")) ?? string.Empty;
                    nestedTool["name"] = string.IsNullOrEmpty(namespaceName)
                        ? bareName
                        : $"{namespaceName}{NamespaceSeparator}{bareName}";
                    nestedTool["namespace"] = namespaceName;
                    result.Add(nestedTool);
                }
            }

            return result;
        }

        if (toolType == "function")
        {
            return
            [
                Obj(
                    ("name", GetValue(tool, "name")),
                    ("description", GetValue(tool, "description") ?? string.Empty),
                    ("parameters", GetValue(tool, "parameters") ?? new Dictionary<string, object?>()),
                    ("native_type", "function"))
            ];
        }

        if (toolType == "mcp")
        {
            return [ResponsesMcpToolToCanonical(tool)];
        }

        if (toolType == "web_search")
        {
            return
            [
                Obj(
                    ("name", "web_search"),
                    ("description", GetValue(tool, "description") ?? "Search the web for current information."),
                    ("parameters", Obj(
                        ("type", "object"),
                        ("additionalProperties", false),
                        ("properties", Obj(
                            ("query", Obj(
                                ("type", "string"),
                                ("description", "The web search query."))))),
                        ("required", new List<object?> { "query" }))),
                    ("native_type", "web_search"),
                    ("raw", DeepCopy(tool)))
            ];
        }

        if (toolType == "custom" && IsApplyPatchName(toolName.Replace("-", "_", StringComparison.Ordinal)))
        {
            return [WrapNativeTool("apply_patch", tool, compat)];
        }

        return [WrapNativeTool(toolType, tool, compat)];
    }

    private static Dictionary<string, object?> WrapNativeTool(string toolType, Dictionary<string, object?> tool, IReadOnlyDictionary<string, object?>? compat = null)
    {
        var name = Convert.ToString(GetValue(tool, "name"));
        if (string.IsNullOrEmpty(name))
        {
            name = toolType;
        }

        name = name.Replace("-", "_", StringComparison.Ordinal);

        var providedSchema = GetValue(tool, "parameters")
            ?? GetValue(tool, "input_schema")
            ?? GetValue(tool, "schema");

        Dictionary<string, object?> parameters;
        if (TryAsObject(providedSchema, out var explicitSchema) && explicitSchema.Count > 0)
        {
            parameters = explicitSchema;
        }
        else if (toolType is "local_shell" or "shell")
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("cmd", Obj(("type", "string"))))),
                ("required", new List<object?> { "cmd" }));
        }
        else if (toolType == "apply_patch")
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("patch", Obj(("type", "string"))))),
                ("required", new List<object?> { "patch" }));
        }
        else if (toolType is "custom" or "custom_tool")
        {
            parameters = BuildCustomFreeformParameters(tool);
        }
        else
        {
            parameters = Obj(
                ("type", "object"),
                ("properties", Obj(("input", Obj(("type", "string"))))),
                ("required", new List<object?> { "input" }),
                ("additionalProperties", false));
        }

        return Obj(
            ("name", name),
            ("description", toolType == "apply_patch"
                ? "Apply file edits using patch text. The patch must start with '*** Begin Patch' and end with '*** End Patch'. Use '*** Add File: <path>' with '+' lines, '*** Update File: <path>' with '@@' context blocks and '+'/'-' lines, or '*** Delete File: <path>'. Use `grep -n` to verify exact content before editing. Context lines must match the file exactly, including leading whitespace; copy actual lines, do not retype from memory. If a patch fails to apply, re-read the exact file content before retrying."
                : GetValue(tool, "description") ?? $"Wrapped Responses tool: {toolType}"),
            ("parameters", parameters),
            ("native_type", toolType),
            ("compat", compat ?? GetValue(tool, "compat") ?? new Dictionary<string, object?>()),
            ("raw", DeepCopy(tool)));
    }


    private static Dictionary<string, object?> BuildCustomFreeformParameters(Dictionary<string, object?> tool)
    {
        var format = ObjectValue(tool, "format");
        var formatType = GetString(format, "type") ?? string.Empty;
        var inputDescription = "Freeform input for this custom tool.";

        if (formatType == "grammar")
        {
            var syntax = GetString(format, "syntax") ?? "unknown";
            var definition = Convert.ToString(GetValue(format, "definition")) ?? string.Empty;
            const int maxDefinitionChars = 4000;
            var definitionPart = definition.Length > maxDefinitionChars
                ? definition[..maxDefinitionChars] + "\n...[truncated]"
                : definition;
            inputDescription =
                $"Freeform input that must match the tool grammar (syntax={syntax}).\n" +
                $"Grammar definition:\n{definitionPart}";
        }
        else if (formatType == "text")
        {
            inputDescription = "Freeform text input for this custom tool.";
        }

        return Obj(
            ("type", "object"),
            ("properties", Obj(
                ("input", Obj(
                    ("type", "string"),
                    ("description", inputDescription))))),
            ("required", new List<object?> { "input" }),
            ("additionalProperties", false));
    }

    private static Dictionary<string, object?> RewriteApplyPatchToolDescription(Dictionary<string, object?> tool)
    {
        var nativeType = GetString(tool, "native_type") ?? string.Empty;
        if (nativeType != "apply_patch")
        {
            return tool;
        }

        var compat = ObjectValue(tool, "compat");
        if (!IsTruthy(GetValue(compat, "enable_apply_patch_prompt_compat")))
        {
            return tool;
        }

        var rewritten = new Dictionary<string, object?>(tool, StringComparer.Ordinal);
        rewritten["description"] = string.Join("\n", new[]
        {
            "Apply file edits using patch text only.",
            "Return only the patch payload for this tool call. Do not add explanations, Markdown code fences, JSON wrappers, or command arrays.",
            "The patch must start with '*** Begin Patch' and end with '*** End Patch'.",
            "Do not use unified diff headers such as '---', '+++', or '***************'.",
            "The '@@' line must contain only '@@'. Never add line numbers like '-1,4 +1,8 @@'.",
            "Before editing, use `grep -n` or `rg -n` to verify exact file content and line numbers; never edit based on remembered line numbers.",
            "Context lines in '@@' blocks must match the file exactly, character-for-character, including leading whitespace (spaces vs tabs). Copy actual lines from the file, do not retype from memory.",
            "If a patch fails to apply, re-read the exact file content with `grep -n` or `sed -n` before retrying; do not guess what the file looks like.",
            "Supported operations:",
            "- '*** Add File: <path>' followed by '+' lines.",
            "- '*** Update File: <path>' followed by at least one '@@' block with context, '+' and '-' lines.",
            "- '*** Delete File: <path>'",
            "Correct add-file example:",
            "*** Begin Patch",
            "*** Add File: src/example.txt",
            "+hello",
            "+world",
            "*** End Patch",
            "Correct update-file example:",
            "*** Begin Patch",
            "*** Update File: src/example.txt",
            "@@",
            " old line",
            "-line to replace",
            "+replacement line",
            " another old line",
            "*** End Patch",
            "Correct delete-file example:",
            "*** Begin Patch",
            "*** Delete File: src/obsolete.txt",
            "*** End Patch"
        });
        return rewritten;
    }

    private static List<object?> DedupeCanonicalTools(List<object?> tools)
    {
        var result = new List<object?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in tools)
        {
            if (!TryAsObject(item, out var tool))
            {
                continue;
            }

            var nativeType = GetString(tool, "native_type") ?? "function";
            var name = Convert.ToString(GetValue(tool, "name")) ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var scope = nativeType == "function" ? "function" : nativeType;
            if (!seen.Add($"{scope}\u001f{name}"))
            {
                continue;
            }

            result.Add(tool);
        }

        return result;
    }

    private static object? ToolChoiceToChat(object? toolChoice)
    {
        if (toolChoice is string)
        {
            return toolChoice;
        }

        if (TryAsObject(toolChoice, out var toolChoiceObject))
        {
            if (IsApplyPatchToolChoice(toolChoiceObject))
            {
                return Obj(
                    ("type", "function"),
                    ("function", Obj(("name", "apply_patch"))));
            }

            if (IsWebSearchToolChoice(toolChoiceObject))
            {
                return Obj(
                    ("type", "function"),
                    ("function", Obj(("name", "web_search"))));
            }

            var function = ObjectValue(toolChoiceObject, "function");
            if (HasNonNullValue(function, "name"))
            {
                return toolChoice;
            }

            var type = GetString(toolChoiceObject, "type");
            if (type == "function" && HasNonNullValue(toolChoiceObject, "name"))
            {
                return Obj(
                    ("type", "function"),
                    ("function", Obj(("name", GetValue(toolChoiceObject, "name")))));
            }

            if (type == "custom" && HasNonNullValue(toolChoiceObject, "name"))
            {
                return Obj(
                    ("type", "custom"),
                    ("custom", Obj(("name", GetValue(toolChoiceObject, "name")))));
            }

            if (type is "auto" or "none")
            {
                return type;
            }

            if (type is "required" or "tool" or "any" or "function")
            {
                return "required";
            }
        }

        return toolChoice;
    }

    private static object? ToolChoiceToResponses(object? toolChoice)
    {
        if (toolChoice is string text)
        {
            return text switch
            {
                "any" or "tool" => "required",
                _ => text
            };
        }

        if (!TryAsObject(toolChoice, out var choice))
        {
            return toolChoice;
        }

        var type = GetString(choice, "type") ?? string.Empty;
        if (type == "tool" && HasNonNullValue(choice, "name"))
        {
            return Obj(("type", "function"), ("name", GetValue(choice, "name")));
        }

        if (type is "function" or "custom")
        {
            var nested = ObjectValue(choice, type);
            var name = GetValue(choice, "name") ?? GetValue(nested, "name");
            if (name is not null)
            {
                return Obj(("type", type), ("name", name));
            }
        }

        if (type is "auto" or "none" or "required")
        {
            return type;
        }

        if (type == "any")
        {
            return "required";
        }

        return DeepCopy(choice);
    }

    private static object? ToolChoiceToMessages(object? toolChoice)
    {
        if (toolChoice is string text)
        {
            return text switch
            {
                "none" => Obj(("type", "none")),
                "required" or "any" or "tool" => Obj(("type", "any")),
                _ => Obj(("type", "auto"))
            };
        }

        if (!TryAsObject(toolChoice, out var choice))
        {
            return toolChoice;
        }

        if (IsApplyPatchToolChoice(choice))
        {
            return Obj(("type", "tool"), ("name", "apply_patch"));
        }

        if (IsWebSearchToolChoice(choice))
        {
            return Obj(("type", "tool"), ("name", "web_search"));
        }

        var type = GetString(choice, "type") ?? string.Empty;
        if (type == "tool" && HasNonNullValue(choice, "name"))
        {
            return DeepCopy(choice);
        }

        if (type is "function" or "custom")
        {
            var nested = ObjectValue(choice, type);
            var name = GetValue(choice, "name") ?? GetValue(nested, "name");
            if (name is not null)
            {
                return Obj(("type", "tool"), ("name", name));
            }
        }

        return type switch
        {
            "none" => Obj(("type", "none")),
            "required" or "any" => Obj(("type", "any")),
            _ => Obj(("type", "auto"))
        };
    }

    private static bool IsApplyPatchToolChoice(Dictionary<string, object?> toolChoiceObject)
    {
        var type = GetString(toolChoiceObject, "type") ?? string.Empty;
        if (type == "apply_patch")
        {
            return true;
        }

        if (type != "custom")
        {
            return false;
        }

        var name = GetString(toolChoiceObject, "name");
        if (string.IsNullOrEmpty(name))
        {
            name = GetString(ObjectValue(toolChoiceObject, "custom"), "name");
        }

        return !string.IsNullOrEmpty(name)
            && IsApplyPatchName(name.Replace("-", "_", StringComparison.Ordinal));
    }
}
