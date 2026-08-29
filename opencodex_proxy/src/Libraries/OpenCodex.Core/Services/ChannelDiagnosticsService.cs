using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService : IChannelDiagnosticsService
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IUpstreamClient _upstreamClient;
    private readonly IUpstreamModelClient _upstreamModelClient;
    private readonly IProxyLogService _logs;
    private readonly IChannelService _channels;

    public ChannelDiagnosticsService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IUpstreamClient upstreamClient,
        IUpstreamModelClient upstreamModelClient,
        IProxyLogService logs,
        IChannelService channels)
    {
        _settingsProvider = settingsProvider;
        _upstreamClient = upstreamClient;
        _upstreamModelClient = upstreamModelClient;
        _logs = logs;
        _channels = channels;
    }

    public async Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var draft = ExtractChannelFromBody(body);
            RejectEnvironmentPlaceholders(draft);
            EnsurePublicBaseUrl(draft);
            var validated = ConfigValidator.ValidateChannel(draft, DefaultTimeout());
            var clamped = ClampDiagnosticsChannel(validated);
            var raw = await _upstreamModelClient.ListModelsAsync(
                clamped,
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

    public async Task StreamTestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        SessionUser user,
        ProxyRequestMetadata requestMetadata,
        IProxyStreamWriter writer,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Dictionary<string, object?>? channel = null;
        Dictionary<string, object?>? payload = null;
        Dictionary<string, object?>? compatibleRequest = null;
        Dictionary<string, object?>? upstreamResponse = null;
        object? errorResponse = null;
        string? originalModel = null;
        string? upstreamModel = null;
        string? channelType = null;
        string? channelId = null;
        var statusCode = 200;
        string? error = null;
        StreamWriteMetrics? metrics = null;
        StreamResponseCapture? responseCapture = null;
        var captureTermination = StreamCaptureTermination.UnexpectedEnd;

        writer.PrepareSse();
        try
        {
            var channelIdGuid = ReadChannelId(body);
            var channelResult = await _channels.ReadChannelForDiagnostics(channelIdGuid);
            if (!channelResult.Succeeded || channelResult.Payload is null)
            {
                throw new ProxyException("channel not found", 404);
            }

            var channelConfig = ChannelDtoToConfig(channelResult.Payload);
            var expanded = ConfigEnvironmentExpander.Expand(channelConfig);
            if (!ConfigValue.TryAsObject(expanded, out var expandedChannel))
            {
                throw new ConfigException("expanded config must be an object");
            }

            var validated = ConfigValidator.ValidateChannel(expandedChannel, DefaultTimeout());
            var clampedChannel = ClampDiagnosticsChannel(validated);
            var clampedBody = body.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            ClampDiagnosticsPayloadInputs(clampedBody);
            var testPayload = BuildPayloadFromFlat(
                clampedBody,
                JsonDictionaryValue.String(clampedChannel, "type"));
            var prepared = PrepareTestChannel(clampedChannel, testPayload, requestMetadata);
            channel = prepared.Channel;
            payload = prepared.Payload;
            compatibleRequest = prepared.CompatibleRequest;
            originalModel = prepared.OriginalModel;
            upstreamModel = prepared.UpstreamModel;
            channelType = prepared.ChannelType;
            channelId = JsonDictionaryValue.String(channel, "id");

            responseCapture = new StreamResponseCapture(channelType);
            var upstreamLines = _upstreamClient.StreamJsonAsync(
                channel,
                compatibleRequest,
                DefaultTimeout(),
                cancellationToken);
            var observedUpstreamLines = ProxyStreamService.CapturePassThroughResponse(
                upstreamLines,
                responseCapture,
                cancellationToken);
            // chat/messages 渠道的客户端输出仍转换为 responses 协议；
            // 渠道诊断日志则由转换前的透明观察器记录原始上游响应。
            var converted = new ConvertedStreamResult();
            IAsyncEnumerable<string> observableLines = channelType switch
            {
                ProtocolConverter.Chat => SseStreamConverter.ChatToResponsesEvents(
                    observedUpstreamLines, originalModel, converted, cancellationToken),
                ProtocolConverter.Messages => SseStreamConverter.MessagesToResponsesEvents(
                    observedUpstreamLines, originalModel, converted, cancellationToken),
                _ => observedUpstreamLines
            };
            metrics = await writer.WriteLinesAsync(
                AppendTestCompletedEventAsync(
                    observableLines,
                    () =>
                    {
                        captureTermination = StreamCaptureTermination.Completed;
                        upstreamResponse = responseCapture
                            .Complete(captureTermination)
                            .Response;
                        return BuildTestCompletedEvent(
                            started,
                            statusCode,
                            compatibleRequest,
                            upstreamResponse,
                            null,
                            errorResponse,
                            originalModel,
                            upstreamModel,
                            channelId,
                            channelType,
                            error);
                    },
                    cancellationToken),
                static line => line.Trim().Length > 0,
                () => ElapsedMilliseconds(started),
                cancellationToken);
            captureTermination = StreamCaptureTermination.Completed;
            upstreamResponse = responseCapture
                .Complete(captureTermination)
                .Response;
        }
        catch (ConfigException exception)
        {
            captureTermination = StreamCaptureTermination.UpstreamError;
            upstreamResponse ??= responseCapture?
                .Complete(captureTermination)
                .Response;
            statusCode = 400;
            error = exception.Message;
            errorResponse = BuildErrorResponse(error, "config_error");
            await WriteSseEventAsync(
                "channel_test.error",
                errorResponse,
                cancellationToken);
            await WriteSseEventAsync(
                "channel_test.completed",
                BuildTestCompletedEvent(
                    started,
                    statusCode,
                    compatibleRequest,
                    upstreamResponse,
                    null,
                    errorResponse,
                    originalModel,
                    upstreamModel,
                    channelId,
                    channelType,
                    error),
                cancellationToken);
        }
        catch (ProxyException exception)
        {
            captureTermination = StreamCaptureTermination.UpstreamError;
            upstreamResponse ??= responseCapture?
                .Complete(captureTermination)
                .Response;
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            if (upstreamResponse is null
                && exception is UpstreamException { Body: not null } upstream)
            {
                upstreamResponse = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error"] = upstream.Body
                };
            }

            await WriteSseEventAsync(
                "channel_test.error",
                errorResponse,
                cancellationToken);
            await WriteSseEventAsync(
                "channel_test.completed",
                BuildTestCompletedEvent(
                    started,
                    statusCode,
                    compatibleRequest,
                    upstreamResponse,
                    null,
                    errorResponse,
                    originalModel,
                    upstreamModel,
                    channelId,
                    channelType,
                    error),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            captureTermination = StreamCaptureTermination.ClientCancelled;
            upstreamResponse ??= responseCapture?
                .Complete(captureTermination)
                .Response;
            throw;
        }
        catch
        {
            captureTermination = StreamCaptureTermination.UpstreamError;
            upstreamResponse ??= responseCapture?
                .Complete(captureTermination)
                .Response;
            throw;
        }
        finally
        {
            if (responseCapture is not null)
            {
                upstreamResponse ??= responseCapture
                    .Complete(captureTermination)
                    .Response;
            }

            await WriteTestChannelLogAsync(
                channel,
                user,
                requestMetadata,
                started,
                payload,
                compatibleRequest,
                upstreamResponse,
                null,
                errorResponse,
                originalModel,
                upstreamModel,
                channelId,
                channelType,
                statusCode,
                error,
                isStream: true,
                streamWriteMetrics: metrics);
        }

        async Task WriteSseEventAsync(string eventName, object data, CancellationToken token)
        {
            await writer.WriteLinesAsync(
                Lines(SseEventLines(eventName, data), token),
                static _ => false,
                () => ElapsedMilliseconds(started),
                token);
        }
    }

    private TestChannelPreparedRequest PrepareTestChannel(
        Dictionary<string, object?> channel,
        Dictionary<string, object?> payload,
        ProxyRequestMetadata requestMetadata)
    {
        var channelType = JsonDictionaryValue.String(channel, "type");
        var (originalModel, upstreamModel) = TestModels(channel, JsonDictionaryValue.Get(payload, "model"));
        var route = new ProxyRouteDto(
            channel,
            originalModel,
            upstreamModel,
            supportsImage: true,
            matchedModelMapping: false);
        route = ProxyEndpointService.ApplyResponsesPassthroughHeaders(
            route,
            ProtocolConverter.Responses,
            channelType,
            requestMetadata);
        channel = route.Channel;

        var channelCompat = JsonDictionaryValue.Object(channel, "compat", CloneObject);
        var upstreamRequest = ProtocolConverter.ConvertRequest(
            payload,
            channelType,
            channelType,
            upstreamModel,
            channelCompat);
        upstreamRequest["stream"] = true;
        var compatibleRequest = ApplyCompat(
            upstreamRequest,
            channelCompat);
        compatibleRequest["stream"] = true;
        return new TestChannelPreparedRequest(
            channel,
            payload,
            compatibleRequest,
            originalModel,
            upstreamModel,
            channelType);
    }

    private static async IAsyncEnumerable<string> AppendTestCompletedEventAsync(
        IAsyncEnumerable<string> lines,
        Func<object> buildData,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            yield return line;
        }

        foreach (var line in SseEventLines("channel_test.completed", buildData()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private static IEnumerable<string> SseEventLines(string eventName, object data)
    {
        yield return $"event: {eventName}\n";
        yield return $"data: {JsonSerializer.Serialize(data, StreamJsonOptions)}\n";
        yield return "\n";
    }

    private static async IAsyncEnumerable<string> Lines(
        IEnumerable<string> lines,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
            await Task.CompletedTask;
        }
    }

    private sealed class TestChannelPreparedRequest
    {
        public TestChannelPreparedRequest(
            Dictionary<string, object?> channel,
            Dictionary<string, object?> payload,
            Dictionary<string, object?> compatibleRequest,
            string originalModel,
            string upstreamModel,
            string channelType)
        {
            Channel = channel;
            Payload = payload;
            CompatibleRequest = compatibleRequest;
            OriginalModel = originalModel;
            UpstreamModel = upstreamModel;
            ChannelType = channelType;
        }

        public Dictionary<string, object?> Channel { get; }

        public Dictionary<string, object?> Payload { get; }

        public Dictionary<string, object?> CompatibleRequest { get; }

        public string OriginalModel { get; }

        public string UpstreamModel { get; }

        public string ChannelType { get; }
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
        "authorization_token",
        "access_token",
        "refresh_token",
        "api-key",
        "api_key",
        "apikey",
        "x-api-key",
        "cookie",
        "set-cookie",
        "password"
    };

    private async Task WriteTestChannelLogAsync(
        Dictionary<string, object?>? channelConfig,
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
        string? error,
        bool isStream = false,
        StreamWriteMetrics? streamWriteMetrics = null)
    {
        await _logs.WriteLogAsync(
            new ProxyLogContext(
                RandomNumberGenerator.GetHexString(12).ToLowerInvariant(),
                user.Username,
                ApiKeyId: null,
                Payload: channelConfig,
                UpstreamRequest: compatibleRequest,
                UpstreamResponse: upstreamResponse,
                ResponsePayload: responsePayload,
                ErrorResponse: errorResponse,
                RequestModel: originalModel,
                UpstreamModel: upstreamModel,
                ChannelId: channelId,
                ChannelType: channelType,
                IsStream: isStream,
                TtftMs: streamWriteMetrics?.TtftMs,
                StatusCode: statusCode,
                DurationMs: ElapsedMilliseconds(started),
                Error: error,
                WebSearchDetails: null,
                RequestType: ProxyRequestTypes.Diagnostic),
            requestMetadata);
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

    private static Dictionary<string, object?> BuildTestCompletedEvent(
        long started,
        int statusCode,
        Dictionary<string, object?>? upstreamRequest,
        Dictionary<string, object?>? upstreamResponse,
        Dictionary<string, object?>? responsePayload,
        object? errorResponse,
        string? originalModel,
        string? upstreamModel,
        string? channelId,
        string? channelType,
        string? error)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status_code"] = statusCode,
            ["duration_ms"] = ElapsedMilliseconds(started),
            ["request_model"] = originalModel,
            ["upstream_model"] = upstreamModel,
            ["channel_id"] = channelId,
            ["channel_type"] = channelType
        };

        if (upstreamRequest is not null)
        {
            data["upstream_request"] = RedactObject(upstreamRequest);
        }

        if (upstreamResponse is not null)
        {
            data["upstream_response"] = RedactObject(upstreamResponse);
        }

        if (responsePayload is not null)
        {
            data["response"] = RedactObject(responsePayload);
        }

        if (errorResponse is not null)
        {
            data["error_response"] = errorResponse;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            data["error"] = error;
        }

        return data;
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
