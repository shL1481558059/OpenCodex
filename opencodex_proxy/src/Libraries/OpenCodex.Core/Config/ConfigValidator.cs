namespace OpenCodex.Core.Config;

public static class ConfigValidator
{
    public static void Validate(IReadOnlyDictionary<string, object?> config, int defaultTimeout = 120)
    {
        var unsupportedFields = config.Keys
            .Except(OpenCodexConfig.ConfigFields, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unsupportedFields.Count > 0)
        {
            throw new ConfigException($"unsupported config field(s): {string.Join(", ", unsupportedFields)}");
        }

        var channelsValue = config.TryGetValue("channels", out var value) ? value : new List<object?>();
        if (!ConfigValue.TryAsList(channelsValue, out var channels))
        {
            throw new ConfigException("channels must be a list");
        }

        var ids = new HashSet<(string OwnerUsername, string ChannelId)>();
        foreach (var channelValue in channels)
        {
            var channel = ValidateChannel(channelValue, defaultTimeout);
            var channelId = ConfigValue.PythonString(channel["id"]).Trim();
            var ownerUsername = ConfigValue.PythonString(GetValue(channel, "owner_username", string.Empty)).Trim();
            var key = (ownerUsername, channelId);
            if (!ids.Add(key))
            {
                throw new ConfigException($"duplicated channel id: {channelId}");
            }
        }
    }

    public static Dictionary<string, object?> ValidateChannel(object? channelValue, int defaultTimeout = 120)
    {
        if (!ConfigValue.TryAsObject(channelValue, out var channel))
        {
            throw new ConfigException("each channel must be an object");
        }

        var channelId = ConfigValue.PythonString(GetValue(channel, "id", string.Empty)).Trim();
        if (channelId.Length == 0)
        {
            throw new ConfigException("channel.id is required");
        }

        var channelType = ConfigValue.PythonString(GetValue(channel, "type", string.Empty)).Trim();
        if (!OpenCodexConfig.ChannelTypes.Contains(channelType))
        {
            throw new ConfigException(
                $"channel {channelId} type must be one of {ConfigValue.PythonList(OpenCodexConfig.ChannelTypes.Order(StringComparer.Ordinal))}");
        }

        var baseUrl = ConfigValue.PythonString(GetValue(channel, "baseurl", string.Empty)).Trim();
        if (baseUrl.Length == 0)
        {
            throw new ConfigException($"channel {channelId} baseurl is required");
        }

        if (!baseUrl.StartsWith("http://", StringComparison.Ordinal)
            && !baseUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new ConfigException($"channel {channelId} baseurl must start with http(s)://");
        }

        var authMode = ConfigValue.PythonString(GetValue(channel, "auth_mode", "config")).Trim();
        if (!OpenCodexConfig.AuthModes.Contains(authMode))
        {
            throw new ConfigException($"channel {channelId} auth_mode is invalid");
        }

        var timeout = GetValue(channel, "timeout_seconds", defaultTimeout);
        if (timeout is not int timeoutSeconds || timeoutSeconds <= 0)
        {
            throw new ConfigException($"channel {channelId} timeout_seconds must be positive");
        }

        var retryCount = GetValue(channel, "retry_count", OpenCodexConfig.DefaultRetryCount);
        if (retryCount is not int retryCountValue || retryCountValue < 0)
        {
            throw new ConfigException($"channel {channelId} retry_count must be a non-negative integer");
        }

        var headers = GetValue(channel, "headers", new Dictionary<string, object?>());
        if (!ConfigValue.TryAsObject(headers, out _))
        {
            throw new ConfigException($"channel {channelId} headers must be an object");
        }

        var enabled = GetValue(channel, "enabled", true);
        if (enabled is not bool)
        {
            throw new ConfigException($"channel {channelId} enabled must be a boolean");
        }

        ValidateModelMappings(GetValue(channel, "models", new List<object?>()), channelId);
        ValidateCompat(GetValue(channel, "compat", new Dictionary<string, object?>()), channelId);

        return channel;
    }

    public static void ValidateModelMappings(object? modelsValue, string channelId)
    {
        if (modelsValue is null)
        {
            return;
        }

        if (!ConfigValue.TryAsList(modelsValue, out var models))
        {
            throw new ConfigException($"channel {channelId} models must be a list");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < models.Count; index++)
        {
            if (!ConfigValue.TryAsObject(models[index], out var mapping))
            {
                throw new ConfigException($"channel {channelId} models[{index + 1}] must be an object");
            }

            var model = ConfigValue.PythonString(GetValue(mapping, "model", string.Empty)).Trim();
            var upstreamModel = ConfigValue.PythonString(GetValue(mapping, "upstream_model", string.Empty)).Trim();
            if (model.Length == 0)
            {
                throw new ConfigException($"channel {channelId} models[{index + 1}].model is required");
            }

            if (upstreamModel.Length == 0)
            {
                mapping["upstream_model"] = model;
            }

            if (!mapping.TryGetValue("supports_image", out var supportsImage))
            {
                mapping["supports_image"] = false;
            }
            else if (supportsImage is not bool)
            {
                throw new ConfigException($"channel {channelId} models[{index + 1}].supports_image must be a boolean");
            }

            if (!seen.Add(model))
            {
                throw new ConfigException($"channel {channelId} duplicated model mapping: {model}");
            }
        }
    }

    public static void ValidateCompat(object? compatValue, string channelId)
    {
        if (compatValue is null)
        {
            return;
        }

        if (!ConfigValue.TryAsObject(compatValue, out var compat))
        {
            throw new ConfigException($"channel {channelId} compat must be an object");
        }

        ValidateCompatFields(compat, $"channel {channelId} compat");
    }

    private static void ValidateCompatFields(Dictionary<string, object?> compat, string label)
    {
        var unsupportedFields = compat.Keys
            .Except(OpenCodexConfig.CompatFields, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unsupportedFields.Count > 0)
        {
            throw new ConfigException($"{label} has unsupported field(s): {string.Join(", ", unsupportedFields)}");
        }

        foreach (var field in new[] { "rename_params", "force_params", "default_params" })
        {
            var value = GetValue(compat, field, new Dictionary<string, object?>());
            if (!ConfigValue.TryAsObject(value, out _))
            {
                throw new ConfigException($"{label}.{field} must be an object");
            }
        }

        foreach (var field in new[] { "drop_params", "drop_tool_types", "unsupported_params" })
        {
            var value = GetValue(compat, field, new List<object?>());
            if (!ConfigValue.TryAsList(value, out _))
            {
                throw new ConfigException($"{label}.{field} must be a list");
            }
        }

        if (compat.TryGetValue("fallback_thinking_on_tool_use", out var fallbackValue)
            && fallbackValue is not bool)
        {
            throw new ConfigException($"{label}.fallback_thinking_on_tool_use must be a boolean");
        }
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key, object? defaultValue)
    {
        return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}
