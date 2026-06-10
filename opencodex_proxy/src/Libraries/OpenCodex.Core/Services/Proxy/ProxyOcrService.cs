using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyOcrService : IProxyOcrService
{
    private const string VisionPath = "/internal/ocr/vision";
    private const string LocalOcrPath = "/internal/ocr/paddleocr";
    private const string LocalOcrModel = "__ocr_paddleocr__";
    private const string LocalChannelId = "__local__";
    private const string LocalDescription = "本地 PaddleOCR 兜底未生成图片描述，仅提取了可见文字";
    private const string VisionPrompt = """
                                      Extract text and describe the visible contents of this image.
                                      Return only strict JSON in the exact shape {"text":"...","description":"..."}.
                                      Requirements:
                                      - "text" must contain verbatim visible text from the image.
                                      - "description" must be short, objective, and only describe visible content.
                                      - If no text is visible, return an empty string for "text".
                                      - Do not wrap the JSON in markdown fences.
                                      """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly ILocalImageOcrService _localOcr;
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyOcrService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        ILocalImageOcrService localOcr,
        IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _upstream = upstream;
        _logs = logs;
        _localOcr = localOcr;
        _settingsProvider = settingsProvider;
    }

    public async Task<ProxyOcrResult> RecognizeAsync(ProxyOcrContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = _settingsProvider.GetSettings();
        var cacheKey = CacheKey(context.Image);
        var sourceKind = context.Image.SourceKind;
        var engine = context.VisionRoute is null ? ProxyOcrEngines.PaddleOcr : ProxyOcrEngines.Vision;
        var requestPath = engine == ProxyOcrEngines.Vision ? VisionPath : LocalOcrPath;
        var requestModel = engine == ProxyOcrEngines.Vision ? context.VisionRoute?.OriginalModel : LocalOcrModel;
        var upstreamModel = engine == ProxyOcrEngines.Vision ? context.VisionRoute?.UpstreamModel : LocalOcrModel;
        var channelId = engine == ProxyOcrEngines.Vision ? ChannelId(context.VisionRoute?.Channel) : LocalChannelId;
        var channelType = engine == ProxyOcrEngines.Vision ? ChannelType(context.VisionRoute?.Channel) : string.Empty;
        Dictionary<string, object?>? requestPayload = null;
        Dictionary<string, object?>? upstreamRequest = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        object? errorResponse = null;
        var statusCode = ProxyHttpStatus.Ok;
        string? error = null;
        var cacheHit = false;

        try
        {
            var cached = TryReadCache(settings, cacheKey);
            if (cached is not null)
            {
                cacheHit = true;
                engine = cached.Engine;
                requestPath = engine == ProxyOcrEngines.Vision ? VisionPath : LocalOcrPath;
                requestModel = engine == ProxyOcrEngines.Vision ? cached.Model : LocalOcrModel;
                upstreamModel = engine == ProxyOcrEngines.Vision ? cached.UpstreamModel : LocalOcrModel;
                channelId = engine == ProxyOcrEngines.Vision ? cached.ChannelId : LocalChannelId;
                channelType = engine == ProxyOcrEngines.Vision ? cached.ChannelType ?? string.Empty : string.Empty;
                requestPayload = CreateOcrRequestPayload(context.Image, requestModel);
                upstreamRequest = engine == ProxyOcrEngines.Vision
                    && !string.IsNullOrWhiteSpace(channelType)
                    && !string.IsNullOrWhiteSpace(upstreamModel)
                    ? ProtocolConverter.ConvertRequest(
                        requestPayload,
                        ProtocolConverter.Responses,
                        channelType,
                        upstreamModel)
                    : requestPayload;
                responsePayload = ResultPayload(cached.Text, cached.Description);
                return new ProxyOcrResult(
                    context.Image.ImageNumber,
                    cached.Text,
                    cached.Description,
                    cached.Engine,
                    cached.SourceKind,
                    cacheHit: true);
            }

            if (context.VisionRoute is not null)
            {
                engine = ProxyOcrEngines.Vision;
                requestPath = VisionPath;
                requestModel = context.VisionRoute.OriginalModel;
                upstreamModel = context.VisionRoute.UpstreamModel;
                channelId = ChannelId(context.VisionRoute.Channel);
                channelType = ChannelType(context.VisionRoute.Channel);
                requestPayload = CreateOcrRequestPayload(context.Image, requestModel);
                upstreamRequest = ProtocolConverter.ConvertRequest(
                    requestPayload,
                    ProtocolConverter.Responses,
                    channelType,
                    upstreamModel);
                var ocrResult = await RecognizeWithVisionAsync(
                    context,
                    requestPayload,
                    upstreamRequest,
                    started);
                upstreamResponse = ocrResult.UpstreamResponse;
                responsePayload = ResultPayload(ocrResult.Text, ocrResult.Description);
                TryWriteCache(settings, cacheKey, new ProxyOcrCacheEntry
                {
                    Engine = ProxyOcrEngines.Vision,
                    SourceKind = sourceKind,
                    Text = ocrResult.Text,
                    Description = ocrResult.Description,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = requestModel,
                    UpstreamModel = upstreamModel,
                    ChannelId = channelId,
                    ChannelType = channelType
                });
                return new ProxyOcrResult(
                    context.Image.ImageNumber,
                    ocrResult.Text,
                    ocrResult.Description,
                    ProxyOcrEngines.Vision,
                    sourceKind,
                    cacheHit: false);
            }

            engine = ProxyOcrEngines.PaddleOcr;
            requestPath = LocalOcrPath;
            requestModel = LocalOcrModel;
            upstreamModel = LocalOcrModel;
            channelId = LocalChannelId;
            channelType = string.Empty;
            requestPayload = CreateOcrRequestPayload(context.Image, LocalOcrModel);
            upstreamRequest = requestPayload;
            if (context.Image.SourceKind == ProxyImageSourceKinds.Url)
            {
                throw new BadRequestException("unsupported image source: remote URL images require a configured vision OCR model");
            }

            var localResult = await RecognizeWithLocalOcrAsync(context.Image, context.CancellationToken);
            responsePayload = ResultPayload(localResult.Text, localResult.Description);
            TryWriteCache(settings, cacheKey, new ProxyOcrCacheEntry
            {
                Engine = ProxyOcrEngines.PaddleOcr,
                SourceKind = sourceKind,
                Text = localResult.Text,
                Description = localResult.Description,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = LocalOcrModel,
                UpstreamModel = LocalOcrModel,
                ChannelId = LocalChannelId,
                ChannelType = string.Empty
            });
            return new ProxyOcrResult(
                context.Image.ImageNumber,
                localResult.Text,
                localResult.Description,
                ProxyOcrEngines.PaddleOcr,
                sourceKind,
                cacheHit: false);
        }
        catch (BadRequestException exception)
        {
            statusCode = exception.StatusCode;
            error = exception.Message;
            errorResponse = exception.ToResponse();
            throw;
        }
        catch (ProxyException exception)
        {
            upstreamResponse = UpstreamErrorBody(exception);
            var wrapped = new UpstreamException(
                $"OCR failed: {exception.Message}",
                ProxyHttpStatus.BadGateway,
                exception.ToResponse(),
                channelId);
            statusCode = wrapped.StatusCode;
            error = wrapped.Message;
            errorResponse = wrapped.ToResponse();
            throw wrapped;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var wrapped = new UpstreamException(
                $"OCR failed: {exception.Message}",
                ProxyHttpStatus.BadGateway,
                body: null,
                channelId);
            statusCode = wrapped.StatusCode;
            error = wrapped.Message;
            errorResponse = wrapped.ToResponse();
            throw wrapped;
        }
        finally
        {
            _logs.WriteLog(new ProxyRequestLogContext(
                context.RequestId,
                context.OwnerUsername,
                context.ApiKeyId,
                requestPayload,
                upstreamRequest,
                upstreamResponse,
                responsePayload,
                errorResponse,
                requestModel,
                upstreamModel,
                channelId,
                channelType,
                isStream: false,
                ttftMs: null,
                statusCode,
                durationMs: ElapsedMilliseconds(started),
                error,
                webSearchDetails: null,
                method: "POST",
                requestPath,
                context.RequestMetadata.ClientIp,
                context.RequestMetadata.Headers,
                requestType: ProxyRequestTypes.Ocr,
                parentRequestLogId: null,
                ocrDetails: OcrDetails(
                    engine,
                    sourceKind,
                    cacheHit,
                    context.RequestId,
                    parentRequestLogId: null)));
        }
    }

    private async Task<VisionOcrExecutionResult> RecognizeWithVisionAsync(
        ProxyOcrContext context,
        Dictionary<string, object?> requestPayload,
        Dictionary<string, object?> upstreamRequest,
        long started)
    {
        try
        {
            var upstreamResponse = await _upstream.PostJsonAsync(
                context.VisionRoute!.Channel,
                upstreamRequest,
                context.DefaultTimeout,
                context.CancellationToken);
            var responsesPayload = ProtocolConverter.ConvertResponse(
                upstreamResponse,
                ProtocolConverter.Responses,
                ChannelType(context.VisionRoute.Channel),
                context.VisionRoute.OriginalModel);
            var responseText = ExtractResponseText(responsesPayload);
            var parsed = ParseVisionResult(responseText);
            return new VisionOcrExecutionResult(
                NormalizeLineEndings(parsed.Text),
                NormalizeLineEndings(parsed.Description),
                upstreamResponse,
                ElapsedMilliseconds(started));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw exception is ProxyException proxyException
                ? new UpstreamException(
                    $"vision OCR failed: {proxyException.Message}",
                    ProxyHttpStatus.BadGateway,
                    (proxyException as UpstreamException)?.Body ?? proxyException.ToResponse(),
                    ChannelId(context.VisionRoute!.Channel))
                : new UpstreamException(
                    $"vision OCR failed: {exception.Message}",
                    ProxyHttpStatus.BadGateway,
                    body: null,
                    ChannelId(context.VisionRoute!.Channel));
        }
    }

    private async Task<LocalOcrExecutionResult> RecognizeWithLocalOcrAsync(
        ProxyImageInput image,
        CancellationToken cancellationToken)
    {
        if (image.ImageBytes is null || image.ImageBytes.Length == 0)
        {
            throw new BadRequestException("unsupported image source: local OCR requires embedded image bytes");
        }

        var text = await _localOcr.RecognizeTextAsync(image.ImageBytes, cancellationToken);
        return new LocalOcrExecutionResult(
            NormalizeLineEndings(text),
            LocalDescription);
    }

    private static Dictionary<string, object?> CreateOcrRequestPayload(
        ProxyImageInput image,
        string? model)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model ?? string.Empty,
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "message",
                    ["role"] = "developer",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "input_text",
                            ["text"] = VisionPrompt
                        }
                    }
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "input_text",
                            ["text"] = "Analyze this image and return the required JSON."
                        },
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "input_image",
                            ["image_url"] = image.ImageReference
                        }
                    }
                }
            }
        };
    }

    private static Dictionary<string, object?> ResultPayload(string text, string description)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["description"] = description
        };
    }

    private static Dictionary<string, object?> OcrDetails(
        string engine,
        string sourceKind,
        bool cacheHit,
        string parentRequestId,
        long? parentRequestLogId)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["engine"] = engine,
            ["image_count"] = 1,
            ["image_sources"] = new List<object?> { sourceKind },
            ["cache_hit"] = cacheHit,
            ["parent_request_id"] = parentRequestId,
            ["parent_request_log_id"] = parentRequestLogId
        };
    }

    private static string ExtractResponseText(Dictionary<string, object?> response)
    {
        var parts = new List<string>();
        if (TryAsList(GetValue(response, "output"), out var outputItems))
        {
            foreach (var item in outputItems)
            {
                if (!TryAsObject(item, out var output))
                {
                    continue;
                }

                if (StringValue(output, "type") == "message")
                {
                    AppendContentText(parts, GetValue(output, "content"));
                    continue;
                }

                if (output.TryGetValue("text", out var textValue))
                {
                    parts.Add(Convert.ToString(textValue) ?? string.Empty);
                }
            }
        }

        if (parts.Count == 0)
        {
            AppendContentText(parts, GetValue(response, "content"));
        }

        if (parts.Count == 0 && response.TryGetValue("text", out var directText))
        {
            parts.Add(Convert.ToString(directText) ?? string.Empty);
        }

        return string.Concat(parts).Trim();
    }

    private static void AppendContentText(List<string> parts, object? content)
    {
        if (content is string text)
        {
            parts.Add(text);
            return;
        }

        if (!TryAsList(content, out var blocks))
        {
            return;
        }

        foreach (var blockItem in blocks)
        {
            if (!TryAsObject(blockItem, out var block))
            {
                parts.Add(Convert.ToString(blockItem) ?? string.Empty);
                continue;
            }

            if (block.TryGetValue("text", out var textValue))
            {
                parts.Add(Convert.ToString(textValue) ?? string.Empty);
            }
        }
    }

    private static VisionResponseJson ParseVisionResult(string responseText)
    {
        var candidate = StripMarkdownCodeFence(responseText);
        if (TryParseVisionJson(candidate, out var parsed))
        {
            return parsed;
        }

        var jsonSlice = ExtractJsonObject(candidate);
        if (TryParseVisionJson(jsonSlice, out parsed))
        {
            return parsed;
        }

        throw new UpstreamException(
            "vision OCR returned invalid JSON",
            ProxyHttpStatus.BadGateway,
            body: responseText);
    }

    private static bool TryParseVisionJson(string candidate, out VisionResponseJson result)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                result = new VisionResponseJson(string.Empty, string.Empty);
                return false;
            }

            var root = document.RootElement;
            result = new VisionResponseJson(
                root.TryGetProperty("text", out var textProperty) ? textProperty.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() ?? string.Empty : string.Empty);
            return true;
        }
        catch (JsonException)
        {
            result = new VisionResponseJson(string.Empty, string.Empty);
            return false;
        }
    }

    private static string StripMarkdownCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstLineEnd)
        {
            return trimmed;
        }

        return trimmed[(firstLineEnd + 1)..lastFence].Trim();
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : text;
    }

    private static ProxyOcrCacheEntry? TryReadCache(OpenCodexRuntimeSettings settings, string cacheKey)
    {
        var path = CacheFilePath(settings, cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<ProxyOcrCacheEntry>(json, JsonOptions);
            return entry is not null && IsSupportedCacheEngine(entry.Engine) ? entry : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteCache(
        OpenCodexRuntimeSettings settings,
        string cacheKey,
        ProxyOcrCacheEntry entry)
    {
        try
        {
            var path = CacheFilePath(settings, cacheKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, JsonOptions));
        }
        catch
        {
        }
    }

    private static string CacheFilePath(OpenCodexRuntimeSettings settings, string cacheKey)
    {
        var root = ResolveCacheRoot(settings);
        return Path.Combine(root, "results", $"{cacheKey}.json");
    }

    private static string ResolveCacheRoot(OpenCodexRuntimeSettings settings)
    {
        if (Path.IsPathRooted(settings.OcrCacheDir))
        {
            return settings.OcrCacheDir;
        }

        var dbDirectory = Path.GetDirectoryName(settings.DbPath);
        return string.IsNullOrWhiteSpace(dbDirectory)
            ? Path.GetFullPath(settings.OcrCacheDir)
            : Path.Combine(dbDirectory, settings.OcrCacheDir);
    }

    private static string CacheKey(ProxyImageInput image)
    {
        var bytes = image.SourceKind == ProxyImageSourceKinds.Data
            ? image.ImageBytes ?? []
            : Encoding.UTF8.GetBytes(image.ImageReference);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool IsSupportedCacheEngine(string? engine)
    {
        return engine == ProxyOcrEngines.Vision || engine == ProxyOcrEngines.PaddleOcr;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }

    private static Dictionary<string, object?>? UpstreamErrorBody(ProxyException exception)
    {
        if (exception is UpstreamException { Body: not null } upstream)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["error"] = upstream.Body
            };
        }

        return null;
    }

    private static string? ChannelId(IReadOnlyDictionary<string, object?>? channel)
    {
        return channel is null ? null : Convert.ToString(GetValue(channel, "id"));
    }

    private static string ChannelType(IReadOnlyDictionary<string, object?>? channel)
    {
        return channel is null ? string.Empty : Convert.ToString(GetValue(channel, "type")) ?? string.Empty;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    private static string StringValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return Convert.ToString(GetValue(dictionary, key)) ?? string.Empty;
    }

    private static bool TryAsObject(object? value, out Dictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> typedDictionary)
        {
            dictionary = typedDictionary;
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            dictionary = readOnlyDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        dictionary = [];
        return false;
    }

    private static bool TryAsList(object? value, out List<object?> list)
    {
        if (value is List<object?> typedList)
        {
            list = typedList;
            return true;
        }

        if (value is IReadOnlyList<object?> readOnlyList)
        {
            list = readOnlyList.ToList();
            return true;
        }

        if (value is IEnumerable<object?> enumerable)
        {
            list = enumerable.ToList();
            return true;
        }

        list = [];
        return false;
    }

    private sealed class ProxyOcrCacheEntry
    {
        public string Engine { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public long CreatedAt { get; set; }

        public string? Model { get; set; }

        public string? UpstreamModel { get; set; }

        public string? ChannelId { get; set; }

        public string? ChannelType { get; set; }
    }

    private sealed class VisionOcrExecutionResult
    {
        public VisionOcrExecutionResult(
            string text,
            string description,
            Dictionary<string, object?> upstreamResponse,
            int durationMs)
        {
            Text = text;
            Description = description;
            UpstreamResponse = upstreamResponse;
            DurationMs = durationMs;
        }

        public string Text { get; }

        public string Description { get; }

        public Dictionary<string, object?> UpstreamResponse { get; }

        public int DurationMs { get; }
    }

    private sealed class LocalOcrExecutionResult
    {
        public LocalOcrExecutionResult(string text, string description)
        {
            Text = text;
            Description = description;
        }

        public string Text { get; }

        public string Description { get; }
    }

    private sealed class VisionResponseJson
    {
        public VisionResponseJson(string text, string description)
        {
            Text = text;
            Description = description;
        }

        public string Text { get; }

        public string Description { get; }
    }
}
