using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Config;
using OpenCodex.Api.Protocols;

namespace OpenCodex.Api.Services;

public sealed partial class AdminChannelDiagnosticsService
{
    private static readonly HashSet<string> ChannelKeys =
    [
        "id",
        "name",
        "type",
        "baseurl",
        "apikey",
        "auth_mode",
        "headers",
        "timeout_seconds",
        "retry_count",
        "compat",
        "models",
        "enabled"
    ];

    private Dictionary<string, object?> DraftChannelFromBody(IReadOnlyDictionary<string, object?> body)
    {
        Dictionary<string, object?> channel;
        if (JsonDictionaryValue.Get(body, "channel") is IReadOnlyDictionary<string, object?> channelObject)
        {
            channel = CloneObject(channelObject);
        }
        else if (body.ContainsKey("baseurl") || body.ContainsKey("type"))
        {
            channel = body
                .Where(pair => ChannelKeys.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => CloneJsonValue(pair.Value),
                    StringComparer.Ordinal);
        }
        else
        {
            throw new ConfigException("channel must be a JSON object");
        }

        var normalized = ConfigNormalizer.Normalize(new Dictionary<string, object?>
        {
            ["channels"] = new List<object?> { channel }
        });
        var expanded = ConfigEnvironmentExpander.Expand(normalized);
        if (!ConfigValue.TryAsObject(expanded, out var expandedObject))
        {
            throw new ConfigException("expanded config must be an object");
        }

        var channels = JsonDictionaryValue.List(expandedObject, "channels");
        var expandedChannel = channels.Count > 0 ? channels[0] : null;
        return ConfigValidator.ValidateChannel(expandedChannel, DefaultTimeout());
    }

    private (Dictionary<string, object?> Channel, Dictionary<string, object?> Payload) ParseTestChannelBody(
        IReadOnlyDictionary<string, object?> body)
    {
        if (JsonDictionaryValue.Get(body, "payload") is IReadOnlyDictionary<string, object?> payload)
        {
            return (DraftChannelFromBody(body), CloneObject(payload));
        }

        if (body.ContainsKey("baseurl") || body.ContainsKey("type"))
        {
            var channel = DraftChannelFromBody(body);
            return (channel, BuildPayloadFromFlat(body, JsonDictionaryValue.String(channel, "type")));
        }

        throw new ConfigException("payload must be a JSON object or a flat channel+payload body is required");
    }

    private static Dictionary<string, object?> BuildPayloadFromFlat(
        IReadOnlyDictionary<string, object?> body,
        string channelType)
    {
        var model = JsonDictionaryValue.String(body, "model");
        var inputText = JsonDictionaryValue.String(body, "input");
        if (inputText.Length == 0)
        {
            inputText = "ping";
        }

        var maxOutputTokens = ToInt(JsonDictionaryValue.Get(body, "max_output_tokens"), 256);
        return channelType switch
        {
            ProtocolConverter.Chat => new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = inputText
                    }
                },
                ["max_tokens"] = maxOutputTokens
            },
            ProtocolConverter.Messages => new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = inputText
                    }
                },
                ["max_tokens"] = maxOutputTokens
            },
            _ => new Dictionary<string, object?>
            {
                ["model"] = model,
                ["input"] = inputText,
                ["max_output_tokens"] = maxOutputTokens
            }
        };
    }
}
