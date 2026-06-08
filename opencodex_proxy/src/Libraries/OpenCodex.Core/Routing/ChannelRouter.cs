using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Routing;

namespace OpenCodex.Core.Routing;

public static class ChannelRouter
{
    public static RouteResult ChooseChannel(IReadOnlyDictionary<string, object?> config, string? model)
    {
        var channels = Channels(config);
        if (channels.Count == 0)
        {
            throw new RoutingException("no enabled channels configured");
        }

        var normalizedModel = (model ?? string.Empty).Trim();
        var enabledChannels = channels.Where(ChannelEnabled).ToList();
        if (enabledChannels.Count == 0)
        {
            throw new RoutingException("no enabled channels configured");
        }

        var hasModelMappings = enabledChannels.Any(channel => ModelMappings(channel).Count > 0);
        if (hasModelMappings)
        {
            foreach (var channel in enabledChannels)
            {
                foreach (var mapping in ModelMappings(channel))
                {
                    if (mapping.TryGetValue("model", out var value)
                        && ConfigValue.PythonString(value) == normalizedModel)
                    {
                        var upstreamModel = mapping.TryGetValue("upstream_model", out var upstreamValue)
                            ? ConfigValue.PythonString(upstreamValue)
                            : string.Empty;

                        if (upstreamModel.Length == 0)
                        {
                            upstreamModel = normalizedModel;
                        }

                        return new RouteResult(channel, normalizedModel, upstreamModel);
                    }
                }
            }

            throw new RoutingException($"no enabled channel configured for model: {normalizedModel}");
        }

        var firstChannel = enabledChannels[0];
        return new RouteResult(firstChannel, normalizedModel, normalizedModel);
    }

    private static List<Dictionary<string, object?>> Channels(IReadOnlyDictionary<string, object?> config)
    {
        if (!config.TryGetValue("channels", out var channelsValue)
            || !ConfigValue.TryAsList(channelsValue, out var values))
        {
            return [];
        }

        return values
            .Where(value => ConfigValue.TryAsObject(value, out _))
            .Select(ConfigValue.AsObject)
            .ToList();
    }

    private static List<Dictionary<string, object?>> ModelMappings(Dictionary<string, object?> channel)
    {
        if (!channel.TryGetValue("models", out var modelsValue)
            || !ConfigValue.TryAsList(modelsValue, out var values))
        {
            return [];
        }

        return values
            .Where(value => ConfigValue.TryAsObject(value, out _))
            .Select(ConfigValue.AsObject)
            .ToList();
    }

    private static bool ChannelEnabled(Dictionary<string, object?> channel)
    {
        return !channel.TryGetValue("enabled", out var enabled) || enabled is not false;
    }
}
