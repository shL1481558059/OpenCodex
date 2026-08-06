using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private const string NativeRemoteMcpType = "mcp";
    private const string NativeRemoteMcpKind = "remote";
    private const string ResponsesMcpDialect = "responses";
    private const string AnthropicMcpDialect = "anthropic";

    internal static bool IsNativeRemoteMcpCanonicalTool(Dictionary<string, object?> tool)
    {
        return string.Equals(GetString(tool, "native_type"), NativeRemoteMcpType, StringComparison.Ordinal)
            && string.Equals(GetString(tool, "mcp_kind"), NativeRemoteMcpKind, StringComparison.Ordinal);
    }

    internal static bool IsLegacyNamespaceMcpCanonicalTool(Dictionary<string, object?> tool)
    {
        if (IsNativeRemoteMcpCanonicalTool(tool))
        {
            return false;
        }

        var namespaceName = GetString(tool, "namespace") ?? string.Empty;
        var name = GetString(tool, "name") ?? string.Empty;
        return namespaceName.StartsWith("mcp__", StringComparison.Ordinal)
            || name.StartsWith("mcp__", StringComparison.Ordinal);
    }

    internal static void EnsureRemoteMcpToolsConvertible(List<object?> canonicalTools, string targetProtocol)
    {
        foreach (var item in canonicalTools)
        {
            if (!TryAsObject(item, out var tool) || !IsNativeRemoteMcpCanonicalTool(tool))
            {
                continue;
            }

            var convertible = targetProtocol switch
            {
                Responses => TryCanonicalMcpToolToResponses(tool, out _, out var responsesError)
                    ? null
                    : responsesError,
                Messages => TryCanonicalMcpToolToAnthropic(tool, out _, out var messagesError)
                    ? null
                    : messagesError,
                Chat => "Chat Completions has no native remote MCP tool definition; use a Responses/Messages upstream or an explicit proxy-side MCP bridge",
                _ => $"unsupported target protocol: {targetProtocol}"
            };

            if (!string.IsNullOrEmpty(convertible))
            {
                ThrowRemoteMcpConversionError(tool, targetProtocol, convertible);
            }
        }
    }

    internal static List<object?> BuildAnthropicMcpServers(List<object?> canonicalTools)
    {
        var result = new List<object?>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in canonicalTools)
        {
            if (!TryAsObject(item, out var tool) || !IsNativeRemoteMcpCanonicalTool(tool))
            {
                continue;
            }

            var name = GetString(tool, "mcp_server_name")
                ?? GetString(tool, "server_label")
                ?? GetString(tool, "name")
                ?? string.Empty;
            if (name.Length == 0 || !seenNames.Add(name))
            {
                continue;
            }

            if (TryAsObject(GetValue(tool, "mcp_server"), out var rawServer) && rawServer.Count > 0)
            {
                result.Add(DeepCopy(rawServer));
                continue;
            }

            var serverUrl = GetString(tool, "server_url");
            if (string.IsNullOrEmpty(serverUrl))
            {
                ThrowRemoteMcpConversionError(
                    tool,
                    Messages,
                    "Anthropic MCP connector conversion requires server_url; OpenAI connector_id/tunnel_id tools cannot be represented as Anthropic mcp_servers");
            }

            var server = Obj(
                ("type", "url"),
                ("name", name),
                ("url", serverUrl));

            var authorization = GetString(tool, "authorization");
            if (!string.IsNullOrEmpty(authorization))
            {
                server["authorization_token"] = authorization;
            }

            result.Add(server);
        }

        return result;
    }

    internal static List<object?> EnrichCanonicalMcpToolsWithAnthropicServers(
        List<object?> canonicalTools,
        object? mcpServers)
    {
        var serversByName = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var item in AsOptionalList(mcpServers))
        {
            if (!TryAsObject(item, out var server))
            {
                continue;
            }

            var name = GetString(server, "name") ?? string.Empty;
            if (name.Length > 0)
            {
                serversByName[name] = server;
            }
        }

        var result = new List<object?>(canonicalTools.Count);
        foreach (var item in canonicalTools)
        {
            if (!TryAsObject(item, out var tool) || !IsNativeRemoteMcpCanonicalTool(tool))
            {
                result.Add(DeepCopy(item));
                continue;
            }

            var enriched = AsObject(DeepCopy(tool));
            var serverName = GetString(enriched, "mcp_server_name")
                ?? GetString(enriched, "server_label")
                ?? GetString(enriched, "name")
                ?? string.Empty;
            if (!serversByName.TryGetValue(serverName, out var server))
            {
                result.Add(enriched);
                continue;
            }

            enriched["mcp_server"] = DeepCopy(server);
            enriched["server_label"] = serverName;
            if (HasNonNullValue(server, "url"))
            {
                enriched["server_url"] = GetValue(server, "url");
            }

            if (HasNonNullValue(server, "authorization_token"))
            {
                enriched["authorization"] = GetValue(server, "authorization_token");
            }

            var toolConfiguration = ObjectValue(server, "tool_configuration");
            if (HasNonNullValue(toolConfiguration, "enabled"))
            {
                enriched["mcp_server_enabled"] = GetValue(toolConfiguration, "enabled");
            }

            if (HasNonNullValue(toolConfiguration, "allowed_tools"))
            {
                enriched["allowed_tools"] = DeepCopy(GetValue(toolConfiguration, "allowed_tools"));
            }

            result.Add(enriched);
        }

        return result;
    }

    private static bool IsAnthropicMcpToolset(Dictionary<string, object?> tool)
    {
        return string.Equals(GetString(tool, "type"), "mcp_toolset", StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> ResponsesMcpToolToCanonical(Dictionary<string, object?> tool)
    {
        var serverLabel = GetString(tool, "server_label")
            ?? GetString(tool, "name")
            ?? string.Empty;
        if (serverLabel.Length == 0)
        {
            throw new BadRequestException("Responses native MCP tool requires server_label");
        }

        var canonical = Obj(
            ("name", serverLabel),
            ("description", GetValue(tool, "server_description") ?? GetValue(tool, "description") ?? string.Empty),
            ("parameters", new Dictionary<string, object?>()),
            ("native_type", NativeRemoteMcpType),
            ("mcp_kind", NativeRemoteMcpKind),
            ("mcp_dialect", ResponsesMcpDialect),
            ("server_label", serverLabel),
            ("raw", DeepCopy(tool)));

        CopyMcpFields(tool, canonical,
            "server_url",
            "connector_id",
            "tunnel_id",
            "authorization",
            "headers",
            "allowed_tools",
            "require_approval",
            "defer_loading",
            "allowed_callers");
        return canonical;
    }

    private static Dictionary<string, object?> AnthropicMcpToolsetToCanonical(Dictionary<string, object?> tool)
    {
        var serverName = GetString(tool, "mcp_server_name")
            ?? GetString(tool, "name")
            ?? string.Empty;

        var canonical = Obj(
            ("name", serverName),
            ("description", GetValue(tool, "description") ?? string.Empty),
            ("parameters", new Dictionary<string, object?>()),
            ("native_type", NativeRemoteMcpType),
            ("mcp_kind", NativeRemoteMcpKind),
            ("mcp_dialect", AnthropicMcpDialect),
            ("mcp_server_name", serverName),
            ("server_label", serverName),
            ("raw", DeepCopy(tool)));

        CopyMcpFields(tool, canonical, "default_config", "configs");
        return canonical;
    }

    private static bool TryCanonicalMcpToolToResponses(
        Dictionary<string, object?> tool,
        out Dictionary<string, object?> result,
        out string error)
    {
        if (string.Equals(GetString(tool, "mcp_dialect"), ResponsesMcpDialect, StringComparison.Ordinal)
            && TryAsObject(GetValue(tool, "raw"), out var raw)
            && raw.Count > 0)
        {
            result = AsObject(DeepCopy(raw));
            error = string.Empty;
            return true;
        }

        var serverLabel = GetString(tool, "server_label")
            ?? GetString(tool, "mcp_server_name")
            ?? GetString(tool, "name")
            ?? string.Empty;
        var serverUrl = GetString(tool, "server_url");
        var connectorId = GetString(tool, "connector_id");
        var tunnelId = GetString(tool, "tunnel_id");
        if (serverLabel.Length == 0
            || (string.IsNullOrEmpty(serverUrl)
                && string.IsNullOrEmpty(connectorId)
                && string.IsNullOrEmpty(tunnelId)))
        {
            result = [];
            error = "Responses MCP conversion requires server_label and one of server_url, connector_id, or tunnel_id";
            return false;
        }

        if (GetValue(tool, "mcp_server_enabled") is false)
        {
            result = [];
            error = "Responses MCP has no equivalent disabled server configuration; tool_configuration.enabled=false cannot be converted without broadening access";
            return false;
        }

        if (!TryAnthropicMcpConfigsToAllowedTools(tool, out var allowedFromConfigs, out var hasConfigAllowList, out error))
        {
            result = [];
            return false;
        }

        if (!TryMcpAllowedToolNames(
                GetValue(tool, "allowed_tools"),
                out var allowedFromServer,
                out var hasServerAllowList,
                out error))
        {
            result = [];
            return false;
        }

        result = Obj(
            ("type", "mcp"),
            ("server_label", serverLabel),
            ("require_approval", "never"));
        CopyMcpFields(tool, result, "server_url", "connector_id", "tunnel_id");
        CopyMcpFields(tool, result,
            "authorization",
            "headers",
            "require_approval");
        if (hasServerAllowList || hasConfigAllowList)
        {
            var effectiveAllowedTools = hasServerAllowList && hasConfigAllowList
                ? allowedFromServer.Intersect(allowedFromConfigs, StringComparer.Ordinal).ToList()
                : hasServerAllowList ? allowedFromServer : allowedFromConfigs;
            result["allowed_tools"] = effectiveAllowedTools.Cast<object?>().ToList();
        }

        var description = GetString(tool, "description");
        if (!string.IsNullOrEmpty(description))
        {
            result["server_description"] = description;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryCanonicalMcpToolToAnthropic(
        Dictionary<string, object?> tool,
        out Dictionary<string, object?> result,
        out string error)
    {
        if (string.Equals(GetString(tool, "mcp_dialect"), AnthropicMcpDialect, StringComparison.Ordinal)
            && TryAsObject(GetValue(tool, "raw"), out var raw)
            && raw.Count > 0)
        {
            result = AsObject(DeepCopy(raw));
            error = string.Empty;
            return true;
        }

        var serverName = GetString(tool, "mcp_server_name")
            ?? GetString(tool, "server_label")
            ?? GetString(tool, "name")
            ?? string.Empty;
        if (serverName.Length == 0)
        {
            result = [];
            error = "Anthropic mcp_toolset conversion requires mcp_server_name/server_label";
            return false;
        }

        if (string.IsNullOrEmpty(GetString(tool, "server_url")))
        {
            result = [];
            error = "Anthropic mcp_toolset conversion requires server_url so the matching mcp_servers entry can be constructed";
            return false;
        }

        result = Obj(
            ("type", "mcp_toolset"),
            ("mcp_server_name", serverName));
        CopyMcpFields(tool, result, "default_config", "configs");
        if (!HasNonNullValue(result, "default_config") && !HasNonNullValue(result, "configs"))
        {
            if (!TryMcpAllowedToolNames(
                    GetValue(tool, "allowed_tools"),
                    out var toolNames,
                    out var hasAllowedToolsFilter,
                    out error))
            {
                result = [];
                return false;
            }

            if (hasAllowedToolsFilter)
            {
                result["default_config"] = Obj(("enabled", false), ("defer_loading", GetValue(tool, "defer_loading") ?? false));
                var configs = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var toolName in toolNames)
                {
                    configs[toolName] = Obj(("enabled", true));
                }

                result["configs"] = configs;
            }
            else if (HasNonNullValue(tool, "defer_loading"))
            {
                result["default_config"] = Obj(("defer_loading", GetValue(tool, "defer_loading")));
            }
        }

        var requireApproval = GetValue(tool, "require_approval");
        if (requireApproval is not null
            && !string.Equals(Convert.ToString(requireApproval), "never", StringComparison.Ordinal))
        {
            result = [];
            error = "Anthropic MCP connector has no equivalent client approval lifecycle; require_approval must be 'never' for conversion";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryMcpAllowedToolNames(
        object? allowedTools,
        out List<string> toolNames,
        out bool hasFilter,
        out string error)
    {
        toolNames = [];
        hasFilter = allowedTools is not null;
        error = string.Empty;
        if (allowedTools is null)
        {
            return true;
        }

        if (TryAsList(allowedTools, out var list))
        {
            if (list.Any(item => item is not string))
            {
                error = "MCP allowed_tools must contain only tool name strings";
                return false;
            }

            toolNames = list.Cast<string>()
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return true;
        }

        if (TryAsObject(allowedTools, out var filter))
        {
            var unsupportedKeys = filter.Keys
                .Where(key => key is not "tool_names" and not "read_only")
                .ToList();
            if (unsupportedKeys.Count > 0 || GetValue(filter, "read_only") is true)
            {
                error = "Anthropic MCP toolset has no equivalent for the requested composite allowed_tools constraints";
                return false;
            }

            if (!TryAsList(GetValue(filter, "tool_names"), out var names)
                || names.Any(item => item is not string))
            {
                error = "MCP allowed_tools.tool_names must contain only tool name strings";
                return false;
            }

            toolNames = names.Cast<string>()
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return true;
        }

        error = "MCP allowed_tools must be an array of tool names or a supported tool_names filter";
        return false;
    }

    private static bool TryAnthropicMcpConfigsToAllowedTools(
        Dictionary<string, object?> tool,
        out List<string> toolNames,
        out bool hasAllowList,
        out string error)
    {
        toolNames = [];
        hasAllowList = false;
        error = string.Empty;
        var defaultConfig = ObjectValue(tool, "default_config");
        var configs = ObjectValue(tool, "configs");
        if (GetValue(defaultConfig, "enabled") is not false)
        {
            var disabledOverrides = configs
                .Where(entry => TryAsObject(entry.Value, out var config) && GetValue(config, "enabled") is false)
                .Select(entry => entry.Key)
                .ToList();
            if (disabledOverrides.Count > 0)
            {
                error = "Responses MCP allowed_tools cannot represent Anthropic default-enabled configs with disabled tool overrides";
                return false;
            }

            return true;
        }

        hasAllowList = true;
        toolNames = configs
            .Where(entry => TryAsObject(entry.Value, out var config) && GetValue(config, "enabled") is true)
            .Select(entry => entry.Key)
            .ToList();
        return true;
    }

    private static void ThrowRemoteMcpConversionError(
        Dictionary<string, object?> tool,
        string targetProtocol,
        string error)
    {
        var serverLabel = GetString(tool, "server_label")
            ?? GetString(tool, "mcp_server_name")
            ?? GetString(tool, "name")
            ?? "<unknown>";
        throw new BadRequestException(
            $"native remote MCP tool '{serverLabel}' cannot be converted to {targetProtocol}: {error}");
    }

    private static void CopyMcpFields(
        Dictionary<string, object?> source,
        Dictionary<string, object?> target,
        params string[] fields)
    {
        foreach (var field in fields)
        {
            if (HasNonNullValue(source, field))
            {
                target[field] = DeepCopy(GetValue(source, field));
            }
        }
    }
}
