using OpenCodex.Core.Config;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService
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
        "circuit_break_duration_seconds",
        "retry_count",
        "priority",
        "capacity",
        "compat",
        "models",
        "enabled"
    ];

    private Dictionary<string, object?> ExtractChannelFromBody(IReadOnlyDictionary<string, object?> body)
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
        var channels = JsonDictionaryValue.List(normalized, "channels");
        var extractedChannel = channels.Count > 0 ? channels[0] : null;
        if (!ConfigValue.TryAsObject(extractedChannel, out var channelDict))
        {
            throw new ConfigException("channel must be a JSON object");
        }

        return channelDict;
    }

    private static Guid ReadChannelId(IReadOnlyDictionary<string, object?> body)
    {
        var value = JsonDictionaryValue.Get(body, "channel_id");
        if (value is Guid guid)
        {
            return guid;
        }

        if (value is string text && Guid.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return Guid.Empty;
    }

    private static Dictionary<string, object?> ChannelDtoToConfig(ChannelDto channel)
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = channel.OwnerUsername,
            ["id"] = channel.Id,
            ["name"] = channel.Name,
            ["type"] = channel.Type,
            ["baseurl"] = channel.BaseUrl,
            ["apikey"] = channel.ApiKey,
            ["auth_mode"] = channel.AuthMode,
            ["headers"] = channel.Headers,
            ["timeout_seconds"] = channel.TimeoutSeconds,
            ["circuit_break_duration_seconds"] = channel.CircuitBreakDurationSeconds,
            ["retry_count"] = channel.RetryCount,
            ["priority"] = channel.Priority,
            ["capacity"] = channel.Capacity,
            ["compat"] = channel.Compat,
            ["models"] = channel.Models,
            ["enabled"] = channel.Enabled
        };
    }

    private static Dictionary<string, object?> BuildPayloadFromFlat(
        IReadOnlyDictionary<string, object?> body,
        string channelType)
    {
        var model = JsonDictionaryValue.String(body, "model");
        var inputText = JsonDictionaryValue.String(body, "input");
        if (inputText.Length == 0)
        {
            inputText = "你好";
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
                ["max_tokens"] = maxOutputTokens,
                ["stream"] = true
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
                ["max_tokens"] = maxOutputTokens,
                ["stream"] = true
            },
            _ => new Dictionary<string, object?>
            {
                ["model"] = model,
                ["instructions"] = "You are Codex.",
                ["input"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "input_text",
                                ["text"] = inputText
                            }
                        }
                    }
                },
                ["store"] = false,
                ["stream"] = true,
                ["include"] = new List<object?> { "reasoning.encrypted_content" },
                ["parallel_tool_calls"] = true,
                ["tool_choice"] = "auto",
                ["tools"] = new List<object?>(),
                ["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = "low"
                },
                ["text"] = new Dictionary<string, object?>
                {
                    ["verbosity"] = "low"
                },
                ["service_tier"] = "priority",
                ["prompt_cache_key"] = "channel-test",
                ["client_metadata"] = new Dictionary<string, object?>
                {
                    ["x-codex-installation-id"] = "00000000-0000-4000-8000-000000000000",
                    ["x-codex-window-id"] = "test-window"
                }
            }
        };
    }
}
