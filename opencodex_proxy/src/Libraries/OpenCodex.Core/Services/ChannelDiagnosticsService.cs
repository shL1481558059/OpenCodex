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

        writer.PrepareSse();
        try
        {
            var prepared = PrepareTestChannel(body, requestMetadata);
            channel = prepared.Channel;
            payload = prepared.Payload;
            compatibleRequest = prepared.CompatibleRequest;
            originalModel = prepared.OriginalModel;
            upstreamModel = prepared.UpstreamModel;
            channelType = prepared.ChannelType;
            channelId = JsonDictionaryValue.String(channel, "id");

            var capture = new ChannelTestStreamCapture();
            var upstreamLines = _upstreamClient.StreamJsonAsync(
                channel,
                compatibleRequest,
                DefaultTimeout(),
                cancellationToken);
            // chat/messages 渠道的上游流式事件需要转换为 responses 协议事件，
            // 以便 ChannelTestStreamCapture 统一提取 output_text。
            var converted = new ConvertedStreamResult();
            IAsyncEnumerable<string> observableLines = channelType switch
            {
                ProtocolConverter.Chat => SseStreamConverter.ChatToResponsesEvents(
                    upstreamLines, originalModel, converted, cancellationToken),
                ProtocolConverter.Messages => SseStreamConverter.MessagesToResponsesEvents(
                    upstreamLines, originalModel, converted, cancellationToken),
                _ => upstreamLines
            };
            metrics = await writer.WriteLinesAsync(
                AppendTestCompletedEventAsync(
                    CaptureTestStreamAsync(observableLines, capture, cancellationToken),
                    () =>
                    {
                        upstreamResponse = capture.UpstreamResponse ?? converted.UpstreamResponse;
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
            upstreamResponse = capture.UpstreamResponse ?? converted.UpstreamResponse;
        }
        catch (ConfigException exception)
        {
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
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            if (exception is UpstreamException { Body: not null } upstream)
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
        IReadOnlyDictionary<string, object?> body,
        ProxyRequestMetadata requestMetadata)
    {
        var (channel, payload) = ParseTestChannelBody(body);
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
        var compatResult = ApplyCompat(
            upstreamRequest,
            channelCompat);
        var compatibleRequest = compatResult.Payload;
        compatibleRequest["stream"] = true;
        return new TestChannelPreparedRequest(
            channel,
            payload,
            compatibleRequest,
            compatResult.Details,
            originalModel,
            upstreamModel,
            channelType);
    }

    private static async IAsyncEnumerable<string> CaptureTestStreamAsync(
        IAsyncEnumerable<string> lines,
        ChannelTestStreamCapture capture,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            capture.Accept(line);
            yield return line;
        }
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
            List<string> compatDetails,
            string originalModel,
            string upstreamModel,
            string channelType)
        {
            Channel = channel;
            Payload = payload;
            CompatibleRequest = compatibleRequest;
            CompatDetails = compatDetails;
            OriginalModel = originalModel;
            UpstreamModel = upstreamModel;
            ChannelType = channelType;
        }

        public Dictionary<string, object?> Channel { get; }

        public Dictionary<string, object?> Payload { get; }

        public Dictionary<string, object?> CompatibleRequest { get; }

        public List<string> CompatDetails { get; }

        public string OriginalModel { get; }

        public string UpstreamModel { get; }

        public string ChannelType { get; }
    }

    private sealed class ChannelTestStreamCapture
    {
        private readonly List<string> _outputText = [];
        private Dictionary<string, object?>? _response;
        private object? _model;
        private object? _usage;

        public Dictionary<string, object?>? UpstreamResponse
        {
            get
            {
                if (_response is not null)
                {
                    if (_outputText.Count > 0 && !_response.ContainsKey("output_text"))
                    {
                        _response["output_text"] = string.Concat(_outputText);
                    }

                    return _response;
                }

                if (_model is null && _usage is null && _outputText.Count == 0)
                {
                    return null;
                }

                var response = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (_model is not null)
                {
                    response["model"] = _model;
                }

                if (_usage is not null)
                {
                    response["usage"] = _usage;
                }

                if (_outputText.Count > 0)
                {
                    response["output_text"] = string.Concat(_outputText);
                }

                return response;
            }
        }

        public void Accept(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return;
            }

            var json = line["data:".Length..].Trim();
            if (json.Length == 0 || json == "[DONE]")
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                CaptureObject(root);
                var type = ProxyStreamService.TryExtractString(root, "type");
                if (string.Equals(type, "response.output_text.delta", StringComparison.Ordinal)
                    && ProxyStreamService.TryExtractString(root, "delta") is { Length: > 0 } delta)
                {
                    _outputText.Add(delta);
                }

                if (root.TryGetProperty("response", out var responseElement)
                    && responseElement.ValueKind == JsonValueKind.Object)
                {
                    CaptureObject(responseElement);
                    if (string.Equals(type, "response.completed", StringComparison.Ordinal))
                    {
                        _response = ProxyStreamService.FromJsonElement(responseElement) as Dictionary<string, object?>;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        private void CaptureObject(JsonElement element)
        {
            _model ??= ProxyStreamService.TryExtractString(element, "model");
            _usage ??= ProxyStreamService.TryExtractObject(element, "usage");
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
        string? error,
        bool isStream = false,
        StreamWriteMetrics? streamWriteMetrics = null)
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
                IsStream: isStream,
                TtftMs: streamWriteMetrics?.TtftMs,
                StatusCode: statusCode,
                DurationMs: ElapsedMilliseconds(started),
                Error: error,
                WebSearchDetails: null,
                StreamWriteMetrics: streamWriteMetrics),
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
