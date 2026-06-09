using System.Diagnostics;
using System.Security.Cryptography;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService : IChannelDiagnosticsService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IUpstreamClient _upstreamClient;
    private readonly IUpstreamModelClient _upstreamModelClient;
    private readonly IProxyLogService _logs;

    public ChannelDiagnosticsService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IUpstreamClient upstreamClient,
        IUpstreamModelClient upstreamModelClient,
        IProxyLogService logs)
    {
        _settingsProvider = settingsProvider;
        _upstreamClient = upstreamClient;
        _upstreamModelClient = upstreamModelClient;
        _logs = logs;
    }

    public async Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var raw = await _upstreamModelClient.ListModelsAsync(
                DraftChannelFromBody(body),
                DefaultTimeout(),
                cancellationToken);
            return ApiOpResult<DiscoverModelsResponse>.Succeed(DiscoverModelsResponse.From(
                ExtractModelIds(raw),
                raw,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            return ApiOpResult<DiscoverModelsResponse>.Fail(400, exception.Message);
        }
        catch (UpstreamException exception)
        {
            return ApiOpResult<DiscoverModelsResponse>.Fail(
                exception.StatusCode,
                exception.Message);
        }
    }

    public async Task<ApiOpResult<TestChannelResponse>> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        SessionUser user,
        ProxyRequestMetadata requestMetadata,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Dictionary<string, object?>? channel = null;
        Dictionary<string, object?>? payload = null;
        Dictionary<string, object?>? compatibleRequest = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        object? errorResponse = null;
        string? originalModel = null;
        string? upstreamModel = null;
        string? channelType = null;
        string? channelId = null;
        var statusCode = 200;
        string? error = null;

        try
        {
            (channel, payload) = ParseTestChannelBody(body);
            channelType = JsonDictionaryValue.String(channel, "type");
            channelId = JsonDictionaryValue.String(channel, "id");
            (originalModel, upstreamModel) = TestModels(channel, JsonDictionaryValue.Get(payload, "model"));
            var upstreamRequest = ProtocolConverter.ConvertRequest(
                payload,
                channelType,
                channelType,
                upstreamModel);
            var compatResult = ApplyCompat(
                upstreamRequest,
                JsonDictionaryValue.Object(channel, "compat", CloneObject));
            compatibleRequest = compatResult.Payload;
            var compatDetails = compatResult.Details;
            upstreamResponse = await _upstreamClient.PostJsonAsync(
                channel,
                compatibleRequest,
                DefaultTimeout(),
                cancellationToken);
            responsePayload = ProtocolConverter.ConvertResponse(
                upstreamResponse,
                channelType,
                channelType,
                originalModel);

            return ApiOpResult<TestChannelResponse>.Succeed(TestChannelResponse.From(
                originalModel,
                upstreamModel,
                compatDetails,
                responsePayload,
                ElapsedMilliseconds(started)));
        }
        catch (ConfigException exception)
        {
            statusCode = 400;
            error = exception.Message;
            errorResponse = BuildErrorResponse(error, "config_error");
            return ApiOpResult<TestChannelResponse>.Fail(400, exception.Message);
        }
        catch (ProxyException exception)
        {
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            return ApiOpResult<TestChannelResponse>.Fail(
                exception.StatusCode,
                exception.Message);
        }
        finally
        {
            WriteTestChannelLog(
                body,
                user,
                requestMetadata,
                started,
                payload,
                compatibleRequest,
                upstreamResponse,
                responsePayload,
                errorResponse,
                originalModel,
                upstreamModel,
                channelId,
                channelType,
                statusCode,
                error);
        }
    }

    private static (string OriginalModel, string UpstreamModel) TestModels(
        IReadOnlyDictionary<string, object?> channel,
        object? model)
    {
        var originalModel = (model?.ToString() ?? string.Empty).Trim();
        foreach (var item in JsonDictionaryValue.List(channel, "models"))
        {
            if (item is not IReadOnlyDictionary<string, object?> mapping)
            {
                continue;
            }

            if (JsonDictionaryValue.String(mapping, "model") == originalModel)
            {
                var upstreamModel = JsonDictionaryValue.String(mapping, "upstream_model");
                return (originalModel, upstreamModel.Length == 0 ? originalModel : upstreamModel);
            }
        }

        return (originalModel, originalModel);
    }

    private static List<string> ExtractModelIds(IReadOnlyDictionary<string, object?> raw)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in JsonDictionaryValue.List(raw, "data"))
        {
            if (item is not IReadOnlyDictionary<string, object?> model)
            {
                continue;
            }

            var modelId = JsonDictionaryValue.String(model, "id");
            if (modelId.Length > 0 && seen.Add(modelId))
            {
                ids.Add(modelId);
            }
        }

        return ids;
    }

    private int DefaultTimeout()
    {
        return _settingsProvider.GetSettings().DefaultTimeout;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }

    private static readonly HashSet<string> SensitiveLogKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "api-key",
        "api_key",
        "apikey",
        "x-api-key",
        "cookie",
        "set-cookie",
        "password"
    };

    private void WriteTestChannelLog(
        IReadOnlyDictionary<string, object?> originalBody,
        SessionUser user,
        ProxyRequestMetadata requestMetadata,
        long started,
        Dictionary<string, object?>? payload,
        Dictionary<string, object?>? compatibleRequest,
        Dictionary<string, object?>? upstreamResponse,
        Dictionary<string, object?>? responsePayload,
        object? errorResponse,
        string? originalModel,
        string? upstreamModel,
        string? channelId,
        string? channelType,
        int statusCode,
        string? error)
    {
        _logs.WriteLog(
            new ProxyLogContext(
                RandomNumberGenerator.GetHexString(12).ToLowerInvariant(),
                user.Username,
                ApiKeyId: null,
                Payload: RedactObject(originalBody),
                UpstreamRequest: RedactObject(compatibleRequest),
                UpstreamResponse: RedactObject(upstreamResponse),
                ResponsePayload: RedactObject(responsePayload),
                ErrorResponse: errorResponse,
                RequestModel: originalModel,
                UpstreamModel: upstreamModel,
                ChannelId: channelId,
                ChannelType: channelType,
                IsStream: false,
                TtftMs: null,
                StatusCode: statusCode,
                DurationMs: ElapsedMilliseconds(started),
                Error: error,
                WebSearchDetails: null),
            RedactRequestMetadata(requestMetadata));
    }

    private static object BuildErrorResponse(string message, string errorType)
    {
        return new
        {
            error = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["type"] = errorType
            }
        };
    }

    private static ProxyRequestMetadata RedactRequestMetadata(ProxyRequestMetadata requestMetadata)
    {
        return new ProxyRequestMetadata(
            requestMetadata.Method,
            requestMetadata.Path,
            requestMetadata.ClientIp,
            requestMetadata.Headers.ToDictionary(
                pair => pair.Key,
                pair => IsSensitiveLogKey(pair.Key) ? RedactText(pair.Value) : pair.Value,
                StringComparer.Ordinal));
    }

    private static Dictionary<string, object?>? RedactObject(
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return null;
        }

        return source.ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveLogKey(pair.Key)
                ? RedactValue(pair.Value)
                : RedactNestedValue(pair.Value),
            StringComparer.Ordinal);
    }

    private static object? RedactNestedValue(object? value)
    {
        return value switch
        {
            IReadOnlyDictionary<string, object?> dictionary => RedactObject(dictionary),
            IReadOnlyList<object?> list => list.Select(RedactNestedValue).ToList(),
            _ => value
        };
    }

    private static object? RedactValue(object? value)
    {
        return value is null ? null : RedactText(Convert.ToString(value) ?? string.Empty);
    }

    private static string RedactText(string value)
    {
        return value.Length == 0 ? string.Empty : "...";
    }

    private static bool IsSensitiveLogKey(string key)
    {
        return SensitiveLogKeys.Contains(key);
    }
}
