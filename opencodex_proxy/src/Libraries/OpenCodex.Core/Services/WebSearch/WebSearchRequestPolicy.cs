using System.Text.Json;
using System.Text;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.WebSearch;

public static class WebSearchRequestPolicy
{
    public const string ToolName = "web_search";
    public const string InternalToolName = "opencodex_web_search";
    public const string PublicItemPrefix = "ws_ocxp_";
    public const int MaxArgumentBytes = 16_384;
    public const int MaxQueryLength = 2048;

    private const int DefaultMaxWebSearchCalls = 15;

    public static bool DeclaresWebSearchTool(IReadOnlyDictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "tools"), out var tools))
        {
            return false;
        }

        return tools.Any(item =>
            TryAsObject(item, out var tool)
            && IsNativeSearchType(StringValue(tool, "type")));
    }

    public static int MaxWebSearchCalls(IReadOnlyDictionary<string, object?> payload)
    {
        var value = GetValue(payload, "max_tool_calls");
        return value switch
        {
            null => DefaultMaxWebSearchCalls,
            int count when count is >= 0 and <= 64 => count,
            long count when count is >= 0 and <= 64 => (int)count,
            _ => throw new BadRequestException("max_tool_calls for proxy web search must be an integer between 0 and 64")
        };
    }

    public static BuiltinToolRequestContext? RegisterBuiltin(
        Dictionary<string, object?> payload,
        string mode,
        string entryProtocol,
        string channelType,
        string ownerRole,
        Guid ownerUserId = default)
    {
        if (mode != WebSearchModes.Simulate
            || entryProtocol != ProtocolConverter.Responses
            || channelType is not (ProtocolConverter.Chat or ProtocolConverter.Messages)
            || ownerRole != "superadmin"
            || !DeclaresWebSearchTool(payload))
        {
            return null;
        }

        var tools = ListValue(payload, "tools");
        var nativeTools = tools.Where(IsWebSearchTool).Cast<Dictionary<string, object?>>().ToList();
        if (nativeTools.Count != 1)
        {
            throw new BadRequestException("proxy web search requires exactly one native search tool declaration");
        }
        ValidateNativeOptions(nativeTools[0]);

        var occupied = ToolNames(tools).ToHashSet(StringComparer.Ordinal);
        foreach (var item in ListValue(payload, "input").OfType<Dictionary<string, object?>>())
        {
            occupied.UnionWith(ToolNames(ListValue(item, "tools")));
        }
        var name = InternalToolName;
        for (var suffix = 2; occupied.Contains(name); suffix++)
        {
            name = $"{InternalToolName}_{suffix}";
        }

        var index = tools.IndexOf(nativeTools[0]);
        tools[index] = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = "Search the web for current information when needed. Results contain source URLs; cite relevant URLs in the answer.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["query"] = new Dictionary<string, object?> { ["type"] = "string" }
                },
                ["required"] = new List<object?> { "query" },
                ["additionalProperties"] = false
            }
        };
        payload["tools"] = tools;

        var choice = GetValue(payload, "tool_choice");
        HashSet<string>? allowedNames = null;
        if (IsWebSearchToolChoice(choice))
        {
            payload["tool_choice"] = FunctionChoice(name);
            choice = payload["tool_choice"];
        }
        else if (TryAsObject(choice, out var allowed) && StringValue(allowed, "type") == "allowed_tools")
        {
            var choices = ListValue(allowed, "tools")
                .Select(item => IsWebSearchToolChoice(item) ? (object?)FunctionChoice(name) : DeepCopy(item))
                .ToList();
            allowed["tools"] = choices;
            payload["tool_choice"] = allowed;
            allowedNames = choices.Select(ChoiceName).ToHashSet(StringComparer.Ordinal);
            if (allowedNames.Contains(string.Empty))
            {
                throw new BadRequestException("unsupported tool reference in allowed_tools");
            }
            if (allowedNames.Count == 0 && StringValue(allowed, "mode") == "required")
            {
                throw new BadRequestException("required allowed_tools must not be empty");
            }
        }

        var maxCalls = MaxWebSearchCalls(payload);
        var forcedName = ChoiceName(choice);
        var availableNames = ToolNames(tools).Where(tool => allowedNames is null || allowedNames.Contains(tool));
        if (maxCalls == 0 && (forcedName == name
            || (IsRequired(choice) && availableNames.All(tool => tool == name))))
        {
            throw new BadRequestException("max_tool_calls=0 conflicts with required web search");
        }
        var canSearch = maxCalls > 0 && !IsNone(choice)
            && (forcedName.Length == 0 || forcedName == name)
            && (allowedNames is null || allowedNames.Contains(name));
        if (maxCalls == 0)
        {
            tools.RemoveAt(index);
        }

        var include = ListValue(payload, "include");
        var includeSources = include.Contains("web_search_call.action.sources");
        if (include.OfType<string>().Any(item => item.StartsWith("web_search_call.", StringComparison.Ordinal)
            && item != "web_search_call.action.sources"))
        {
            throw new BadRequestException("proxy web search only supports include=web_search_call.action.sources");
        }
        DropWebSearchIncludeItems(payload);

        return new BuiltinToolRequestContext
        {
            OwnerUserId = ownerUserId,
            WebSearchToolName = name,
            MaxWebSearchCalls = maxCalls,
            SearchAllowed = canSearch,
            IncludeSources = includeSources,
            AllowedToolNames = allowedNames
        };
    }

    public static void FinalizeUpstreamRequest(Dictionary<string, object?> request, BuiltinToolRequestContext? binding)
    {
        if (binding is null)
        {
            return;
        }
        var tools = ListValue(request, "tools");
        if (binding.AllowedToolNames is not null)
        {
            tools = tools.Where(item => TryAsObject(item, out var tool)
                && binding.AllowedToolNames.Contains(
                    StringValue(tool, "name", StringValue(ObjectValue(tool, "function"), "name")))).ToList();
            request["tools"] = tools;
        }
        if (binding.SearchAllowed && !tools.Any(item => TryAsObject(item, out var tool)
            && StringValue(tool, "name", StringValue(ObjectValue(tool, "function"), "name")) == binding.WebSearchToolName))
        {
            throw new BadRequestException("proxy web search binding is absent from the upstream tool definitions");
        }
        if (tools.Count == 0)
        {
            if (IsRequired(GetValue(request, "tool_choice")))
            {
                throw new BadRequestException("tool_choice requires a tool, but no allowed tool remains");
            }
            request.Remove("tools");
            request.Remove("tool_choice");
        }
    }

    public static Dictionary<string, object?> ApplyMode(
        IReadOnlyDictionary<string, object?> payload,
        string mode)
    {
        if (mode != WebSearchModes.Disabled)
        {
            return DeepCopyObject(payload);
        }

        var result = DeepCopyObject(payload);
        DropWebSearchTools(result);
        DropWebSearchToolChoice(result);
        DropWebSearchIncludeItems(result);
        return result;
    }

    public static (string? Query, string? Error) ParseQuery(string arguments)
    {
        if (Encoding.UTF8.GetByteCount(arguments) > MaxArgumentBytes)
        {
            return (null, "web_search arguments are too large");
        }
        Dictionary<string, object?> value;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "web_search arguments must be an object");
            }

            var properties = document.RootElement.EnumerateObject().ToList();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
            {
                return (null, "web_search arguments must not contain duplicate keys");
            }
            value = (Dictionary<string, object?>)FromJsonElement(document.RootElement)!;
        }
        catch (JsonException)
        {
            return (null, "web_search arguments must be valid JSON");
        }

        var extraKeys = value.Keys
            .Where(key => !string.Equals(key, "query", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (extraKeys.Count > 0)
        {
            return (null, "web_search only supports the query argument");
        }

        if (GetValue(value, "query") is not string text)
        {
            return (null, "web_search query must be a string");
        }
        var query = text.Trim();
        return query.Length > MaxQueryLength
            ? (null, "web_search query is too long")
            : query.Length == 0
            ? (null, "web_search query is required")
            : (query, null);
    }

    private static void DropWebSearchTools(Dictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "tools"), out var tools))
        {
            return;
        }

        var filtered = tools
            .Where(item => !IsWebSearchTool(item))
            .ToList();
        if (filtered.Count == tools.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("tools");
            return;
        }

        payload["tools"] = filtered;
    }

    private static void DropWebSearchToolChoice(Dictionary<string, object?> payload)
    {
        var toolChoice = GetValue(payload, "tool_choice");
        if (TryAsObject(toolChoice, out var choice) && StringValue(choice, "type") == "allowed_tools")
        {
            choice["tools"] = ListValue(choice, "tools").Where(item => !IsWebSearchToolChoice(item)).ToList();
            if (ListValue(choice, "tools").Count == 0)
            {
                payload["tool_choice"] = "none";
            }
            return;
        }
        if (!IsWebSearchToolChoice(toolChoice))
        {
            return;
        }

        payload.Remove("tool_choice");
    }

    private static void DropWebSearchIncludeItems(Dictionary<string, object?> payload)
    {
        if (!TryAsList(GetValue(payload, "include"), out var includeItems))
        {
            return;
        }

        var filtered = includeItems
            .Where(item => item is not string text || !text.StartsWith("web_search_call.", StringComparison.Ordinal))
            .ToList();
        if (filtered.Count == includeItems.Count)
        {
            return;
        }

        if (filtered.Count == 0)
        {
            payload.Remove("include");
            return;
        }

        payload["include"] = filtered;
    }

    private static bool IsWebSearchTool(object? item)
    {
        if (!TryAsObject(item, out var tool))
        {
            return false;
        }

        return IsNativeSearchType(StringValue(tool, "type"));
    }

    private static bool IsWebSearchToolChoice(object? toolChoice)
    {
        if (toolChoice is string text)
        {
            return IsNativeSearchType(text.Trim());
        }

        return IsWebSearchTool(toolChoice);
    }

    private static bool IsNativeSearchType(string value) => value is "web_search" or "web_search_preview";

    private static Dictionary<string, object?> FunctionChoice(string name) => new()
    {
        ["type"] = "function", ["name"] = name
    };

    private static bool IsNone(object? choice) => choice is "none"
        || (TryAsObject(choice, out var value) && StringValue(value, "type") == "none");

    private static bool IsRequired(object? choice) => choice is "required" or "any"
        || (TryAsObject(choice, out var value) && (StringValue(value, "type") is "required" or "any"
            || StringValue(value, "mode") == "required"));

    private static string ChoiceName(object? choice)
    {
        if (!TryAsObject(choice, out var value))
        {
            return string.Empty;
        }
        var type = StringValue(value, "type");
        if (type is not ("function" or "custom" or "tool"))
        {
            return string.Empty;
        }
        var name = StringValue(value, "name", StringValue(ObjectValue(value, type), "name"));
        var scope = StringValue(value, "namespace");
        return scope.Length > 0 ? $"{scope}__{name}" : name;
    }

    private static IEnumerable<string> ToolNames(IEnumerable<object?> tools, string prefix = "")
    {
        foreach (var tool in tools.OfType<Dictionary<string, object?>>())
        {
            var name = StringValue(tool, "name", StringValue(ObjectValue(tool, "function"), "name"));
            if (StringValue(tool, "type") == "namespace")
            {
                foreach (var nested in ToolNames(ListValue(tool, "tools"), $"{prefix}{name}__"))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return prefix + name;
            }
        }
    }

    private static void ValidateNativeOptions(Dictionary<string, object?> tool)
    {
        foreach (var (key, value) in tool)
        {
            var supported = key is "type" or "description"
                || value is null
                || (key == "external_web_access" && value is true)
                || (key == "search_context_size" && value is "medium")
                || (key == "return_token_budget" && value is "default")
                || (key == "search_content_types" && TryAsList(value, out var types)
                    && types.Count == 1 && types[0] is "text");
            if (!supported)
            {
                throw new BadRequestException($"proxy web search does not support the requested '{key}' option");
            }
        }
    }
}
