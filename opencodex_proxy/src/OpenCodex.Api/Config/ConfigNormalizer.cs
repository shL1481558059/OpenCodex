namespace OpenCodex.Api.Config;

public static class ConfigNormalizer
{
    public static Dictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?> config)
    {
        var candidate = (Dictionary<string, object?>)ConfigValue.DeepCopy(config)!;
        if (!candidate.TryGetValue("channels", out var channelsValue)
            || !ConfigValue.TryAsList(channelsValue, out var channels))
        {
            return candidate;
        }

        candidate["channels"] = channels;
        for (var channelIndex = 0; channelIndex < channels.Count; channelIndex++)
        {
            var channelValue = channels[channelIndex];
            if (!ConfigValue.TryAsObject(channelValue, out var channel))
            {
                continue;
            }

            channels[channelIndex] = channel;

            if (!channel.TryGetValue("models", out var modelsValue)
                || !ConfigValue.TryAsList(modelsValue, out var models))
            {
                continue;
            }

            channel["models"] = models;
            for (var mappingIndex = 0; mappingIndex < models.Count; mappingIndex++)
            {
                var mappingValue = models[mappingIndex];
                if (!ConfigValue.TryAsObject(mappingValue, out var mapping))
                {
                    continue;
                }

                models[mappingIndex] = mapping;

                var model = ConfigValue.PythonString(GetValue(mapping, "model", string.Empty)).Trim();
                var upstreamModel = ConfigValue.PythonString(GetValue(mapping, "upstream_model", string.Empty)).Trim();
                if (upstreamModel.Length == 0)
                {
                    upstreamModel = model;
                }

                mapping["model"] = model;
                mapping["upstream_model"] = upstreamModel;
            }
        }

        return candidate;
    }

    private static object? GetValue(Dictionary<string, object?> dictionary, string key, object? defaultValue)
    {
        return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}
