using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyLogService : IProxyLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IModelCatalogService _catalog;
    private readonly IRepository<RequestLog> _logRepository;
    private readonly IRepository<User> _userRepository;
    private readonly LogContentStore _contentStore;
    private readonly IEventBus? _eventBus;

    public ProxyLogService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IModelCatalogService catalog,
        IOpenCodexDbContext dbContext,
        IRepository<RequestLog> logRepository,
        IRepository<User> userRepository,
        IEventBus? eventBus = null)
    {
        _settingsProvider = settingsProvider;
        _catalog = catalog;
        _logRepository = logRepository;
        _userRepository = userRepository;
        _contentStore = new LogContentStore(dbContext);
        _eventBus = eventBus;
    }

    public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context)
    {
        var settings = _settingsProvider.GetSettings();
        var defaultOwnerUsername = DefaultOwnerUsername(settings);
        var ownerUsername = context.OwnerUsername.Length == 0 ? defaultOwnerUsername : context.OwnerUsername;
        var ownerUserId = ResolveOwnerUserId(ownerUsername);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var log = new RequestLog
        {
            RequestId = context.RequestId,
            CreatedAt = createdAt,
            Method = context.Method,
            Path = context.Path,
            ClientIp = context.ClientIp,
            Model = context.RequestModel,
            RequestType = context.RequestType,
            ParentRequestLogId = context.ParentRequestLogId,
            IsStream = context.IsStream,
            OwnerUserId = ownerUserId,
            ApiKeyId = context.ApiKeyId,
            LifecycleStatus = ProxyRequestLifecycleStatus.Queued
        };
        ApplyConversationMetadata(log, context.RequestHeaders, context.Payload);
        _logRepository.Insert(log);

        _contentStore.Write(log.Id, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestHeaders] = SerializeForLog(context.RequestHeaders),
            [RequestLogContentSlot.RequestBody] = context.RawRequestBody ?? SerializeForLog(context.Payload)
        });
        PublishLogWritten(log.Id, ownerUsername, 0, null);
        return log.Id;
    }

    public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context)
    {
        var settings = _settingsProvider.GetSettings();
        var log = _logRepository.Table.FirstOrDefault(item => item.Id == requestLogId);
        if (log is null)
        {
            return;
        }

        var ownerUsername = context.OwnerUsername.Length == 0
            ? DefaultOwnerUsername(settings)
            : context.OwnerUsername;
        log.OwnerUserId = ResolveOwnerUserId(ownerUsername);
        log.ApiKeyId = context.ApiKeyId;
        log.Model = context.RequestModel ?? log.Model;
        log.UpstreamModel = context.UpstreamModel;
        var channelId = ParseChannelId(context.ChannelId);
        log.ChannelId = channelId;
        log.IsStream = context.IsStream;
        log.LifecycleStatus = ProxyRequestLifecycleStatus.Processing;
        log.ProcessingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _logRepository.Update(log);

        _contentStore.Write(requestLogId, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.UpstreamRequestBody] = SerializeForLog(context.UpstreamRequest)
        });
        PublishLogWritten(requestLogId, ownerUsername, 0, null);
    }

    public async Task CompleteLogAsync(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
    {
        await CompleteLogAsync(requestLogId, new ProxyRequestLogContext(
            context.RequestId,
            context.OwnerUsername,
            context.ApiKeyId,
            context.Payload,
            context.UpstreamRequest,
            context.UpstreamResponse,
            context.ResponsePayload,
            context.ErrorResponse,
            context.RequestModel,
            context.UpstreamModel,
            context.ChannelId,
            context.ChannelType,
            context.IsStream,
            context.TtftMs,
            context.StatusCode,
            context.DurationMs,
            context.Error,
            context.WebSearchDetails,
            request.Method,
            request.Path,
            request.ClientIp,
            request.Headers,
            context.RequestType,
            context.ParentRequestLogId,
            context.OcrDetails,
            request.RawBody,
            context.StreamLines));
    }

    public async Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request)
    {
        return await WriteLogAsync(new ProxyRequestLogContext(
            context.RequestId,
            context.OwnerUsername,
            context.ApiKeyId,
            context.Payload,
            context.UpstreamRequest,
            context.UpstreamResponse,
            context.ResponsePayload,
            context.ErrorResponse,
            context.RequestModel,
            context.UpstreamModel,
            context.ChannelId,
            context.ChannelType,
            context.IsStream,
            context.TtftMs,
            context.StatusCode,
            context.DurationMs,
            context.Error,
            context.WebSearchDetails,
            request.Method,
            request.Path,
            request.ClientIp,
            request.Headers,
            context.RequestType,
            context.ParentRequestLogId,
            context.OcrDetails,
            request.RawBody,
            context.StreamLines));
    }

    public async Task<Guid> WriteLogAsync(ProxyRequestLogContext context)
    {
        var settings = _settingsProvider.GetSettings();
        return await WriteCompletedLogAsync(settings, context);
    }

    // 计费时刻取请求进入网关的时刻(日志 CreatedAt):账单可复算,
    // 长流式请求与多次 attempt 不会因为完成时间不同而落进不同的计费时段。
    // 时间戳缺失或超出可表示范围时退回当前时刻,实际使用的时刻会写进价格快照。
    private static DateTimeOffset BillingInstant(double? unixSeconds)
    {
        if (unixSeconds is not { } seconds || !double.IsFinite(seconds) || seconds <= 0)
        {
            return DateTimeOffset.UtcNow;
        }

        var milliseconds = seconds * 1000d;
        if (milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return DateTimeOffset.UtcNow;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(milliseconds));
    }

    private static double BillingInstantSeconds(DateTimeOffset value)
    {
        return value.ToUnixTimeMilliseconds() / 1000.0;
    }

    private async Task<Guid> CompleteLogAsync(Guid requestLogId, ProxyRequestLogContext context)
    {
        var settings = _settingsProvider.GetSettings();
        var responseForUsage = context.UpstreamResponse ?? [];
        var usage = context.ChannelType is null
            ? new UsageDto(0, 0, 0)
           : ExtractUsage(responseForUsage, context.ChannelType);

       var log = _logRepository.Table.FirstOrDefault(item => item.Id == requestLogId);
        if (log is null)
        {
            return await WriteCompletedLogAsync(settings, context);
        }

        var ownerUsername = context.OwnerUsername.Length == 0
            ? DefaultOwnerUsername(settings)
            : context.OwnerUsername;
        var channelId = ParseChannelId(context.ChannelId);
        var pricing = await _catalog.CalculateCostAsync(
            channelId,
            context.RequestModel,
            context.UpstreamModel,
            new ModelUsageVector(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheWriteTokens,
                usage.CacheReadTokens),
            BillingInstant(log.CreatedAt));
        log.OwnerUserId = ResolveOwnerUserId(ownerUsername);
        log.ApiKeyId = context.ApiKeyId;
        log.Model = context.RequestModel ?? log.Model;
        log.UpstreamModel = context.UpstreamModel;
        log.ChannelId = channelId;
        log.RequestType = context.RequestType;
        log.ParentRequestLogId = context.ParentRequestLogId;
        log.IsStream = context.IsStream;
        log.TtftMs = context.TtftMs;
        log.DurationMs = context.DurationMs;
        log.StatusCode = context.StatusCode;
        log.InputTokens = usage.InputTokens;
        log.CachedTokens = usage.CachedTokens;
        log.CacheWriteTokens = usage.CacheWriteTokens;
        log.CacheReadTokens = usage.CacheReadTokens;
        log.OutputTokens = usage.OutputTokens;
        log.Cost = (double)pricing.Cost;
        log.CostCurrency = pricing.Currency;
        log.PricingModelInfoId = pricing.ModelInfoId;
        log.PricingPlanId = pricing.PricingPlanId;
        log.PricingSnapshotJson = pricing.SnapshotJson;
        log.Error = context.Error;
        log.LifecycleStatus = DetermineLifecycleStatus(context.StatusCode, context.Error);
        log.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        ApplyConversationMetadata(log, context.RequestHeaders, context.Payload);
        _logRepository.Update(log);

        _contentStore.Write(requestLogId, BuildContentValues(context));

        if (context.RequestType == ProxyRequestTypes.Main)
        {
            var childLogs = _logRepository.Table
                .Where(item => item.RequestType == ProxyRequestTypes.Ocr
                    && item.RequestId == context.RequestId
                    && item.ParentRequestLogId == null)
                .ToList();
            if (childLogs.Count > 0)
            {
                foreach (var child in childLogs)
                {
                    child.ParentRequestLogId = log.Id;
                    var childOcrJson = _contentStore.Read(child.Id).Get(RequestLogContentSlot.OcrJson);
                    if (childOcrJson is not null)
                    {
                        _contentStore.Write(child.Id, new Dictionary<RequestLogContentSlot, string?>
                        {
                            [RequestLogContentSlot.OcrJson] = UpdateOcrJsonParentRequestLogId(childOcrJson, log.Id)
                        });
                    }
                }
                foreach (var child in childLogs)
                {
                    _logRepository.Update(child);
                }
            }
        }


        PublishLogWritten(log.Id, ownerUsername, context.StatusCode, context.Error);

        return log.Id;
    }

    private async Task<Guid> WriteCompletedLogAsync(OpenCodexRuntimeSettings settings, ProxyRequestLogContext context)
    {
        var ownerUsername = context.OwnerUsername.Length == 0
            ? DefaultOwnerUsername(settings)
            : context.OwnerUsername;
        var ownerUserId = ResolveOwnerUserId(ownerUsername);
        var responseForUsage = context.UpstreamResponse ?? [];
        var usage = context.ChannelType is null
            ? new UsageDto(0, 0, 0)
           : ExtractUsage(responseForUsage, context.ChannelType);

       var channelId = ParseChannelId(context.ChannelId);
       // 计费时刻与即将落库的 CreatedAt 用同一个值,保证账单可按日志时间原样复算。
       var billingInstant = DateTimeOffset.UtcNow;
       var pricing = await _catalog.CalculateCostAsync(
           channelId,
           context.RequestModel,
           context.UpstreamModel,
           new ModelUsageVector(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheWriteTokens,
                usage.CacheReadTokens),
           billingInstant);

        var logId = WriteRequestLog(
            settings,
            new RequestLogWriteDto(
                context.RequestId,
                BillingInstantSeconds(billingInstant),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                DetermineLifecycleStatus(context.StatusCode, context.Error),
                context.Method,
                context.Path,
                context.ClientIp,
                SerializeForLog(context.RequestHeaders),
                context.RawRequestBody ?? SerializeForLog(context.Payload),
                SerializeForLog(context.UpstreamRequest),
                SerializeForLog(context.UpstreamResponse),
                SerializeForLog(context.ResponsePayload ?? context.ErrorResponse),
                context.WebSearchDetails is null ? null : SerializeForLog(context.WebSearchDetails),
                context.RequestModel,
                context.UpstreamModel,
                channelId,
                context.RequestType,
                context.ParentRequestLogId,
                context.IsStream,
                context.TtftMs,
                context.DurationMs,
                context.StatusCode,
                usage.InputTokens,
                usage.CachedTokens,
                usage.CacheWriteTokens,
                usage.CacheReadTokens,
                usage.OutputTokens,
                (double)pricing.Cost,
                pricing.Currency,
                pricing.ModelInfoId,
                pricing.PricingPlanId,
                pricing.SnapshotJson,
                ownerUserId,
                context.ApiKeyId,
                context.Error,
                context.OcrDetails is null ? null : SerializeForLog(context.OcrDetails),
                context.StreamLines));

        PublishLogWritten(logId, ownerUsername, context.StatusCode, context.Error);
        return logId;
    }

    private static UsageDto ExtractUsage(IReadOnlyDictionary<string, object?> response, string protocol)
    {
        var usage = JsonDictionaryValue.Get(response, "usage");
        if (!TryAsObject(usage, out var usageObject))
        {
            usageObject = [];
        }

        return protocol switch
        {
            "responses" => new UsageDto(
                ToInt(JsonDictionaryValue.Get(usageObject, "input_tokens")),
                CachedTokensFromNestedDetails(usageObject, "input_tokens_details"),
                ToInt(JsonDictionaryValue.Get(usageObject, "output_tokens")),
                0,
                CachedTokensFromNestedDetails(usageObject, "input_tokens_details")),
            "messages" => new UsageDto(
                ToInt(JsonDictionaryValue.Get(usageObject, "input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_creation_input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_read_input_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "cache_creation_input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_read_input_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "output_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "cache_creation_input_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "cache_read_input_tokens"))),
            "chat" => new UsageDto(
                ToInt(JsonDictionaryValue.Get(usageObject, "prompt_tokens")),
                ChatCachedTokens(usageObject),
                ToInt(JsonDictionaryValue.Get(usageObject, "completion_tokens")),
                0,
                ChatCachedTokens(usageObject)),
            _ => new UsageDto(0, 0, 0)
        };
    }

    private Guid WriteRequestLog(
        OpenCodexRuntimeSettings settings,
        RequestLogWriteDto record)
    {
        var log = new RequestLog
        {
            RequestId = record.RequestId,
            CreatedAt = record.CreatedAt,
            ProcessingStartedAt = record.ProcessingStartedAt,
            CompletedAt = record.CompletedAt,
            Method = record.Method,
            Path = record.Path,
            ClientIp = record.ClientIp,
            Model = record.Model,
            UpstreamModel = record.UpstreamModel,
            ChannelId = record.ChannelId,
            RequestType = record.RequestType,
            LifecycleStatus = record.LifecycleStatus,
            ParentRequestLogId = record.ParentRequestLogId,
            IsStream = record.IsStream,
            TtftMs = record.TtftMs,
            DurationMs = record.DurationMs,
            StatusCode = record.StatusCode,
            InputTokens = record.InputTokens,
            CachedTokens = record.CachedTokens,
            CacheWriteTokens = record.CacheWriteTokens,
            CacheReadTokens = record.CacheReadTokens,
            OutputTokens = record.OutputTokens,
            Cost = record.Cost,
            CostCurrency = record.CostCurrency,
            PricingModelInfoId = record.PricingModelInfoId,
            PricingPlanId = record.PricingPlanId,
            PricingSnapshotJson = record.PricingSnapshotJson,
            OwnerUserId = record.OwnerUserId,
            ApiKeyId = record.ApiKeyId,
            Error = record.Error
        };
        ApplyConversationMetadataFromSerializedRequest(log, record.RequestHeaders, record.RequestBody);
        _logRepository.Insert(log);

        _contentStore.Write(log.Id, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestHeaders] = record.RequestHeaders,
            [RequestLogContentSlot.RequestBody] = record.RequestBody,
            [RequestLogContentSlot.UpstreamRequestBody] = record.UpstreamRequestBody,
            [RequestLogContentSlot.UpstreamResponseBody] = record.UpstreamResponseBody,
            [RequestLogContentSlot.ResponseBody] = record.ResponseBody,
            [RequestLogContentSlot.WebSearchJson] = record.WebSearchJson,
            [RequestLogContentSlot.OcrJson] = record.OcrJson,
            [RequestLogContentSlot.StreamLinesJson] = SerializeStreamLines(record.StreamLines)
        });

        if (record.RequestType == ProxyRequestTypes.Main)
        {
            var childLogs = _logRepository.Table
                .Where(item => item.RequestType == ProxyRequestTypes.Ocr
                    && item.RequestId == record.RequestId
                    && item.ParentRequestLogId == null)
                .ToList();
            if (childLogs.Count > 0)
            {
                foreach (var child in childLogs)
                {
                    child.ParentRequestLogId = log.Id;
                    var childOcrJson = _contentStore.Read(child.Id).Get(RequestLogContentSlot.OcrJson);
                    if (childOcrJson is not null)
                    {
                        _contentStore.Write(child.Id, new Dictionary<RequestLogContentSlot, string?>
                        {
                            [RequestLogContentSlot.OcrJson] = UpdateOcrJsonParentRequestLogId(childOcrJson, log.Id)
                        });
                    }
                }
                foreach (var child in childLogs)
                {
                    _logRepository.Update(child);
                }
            }
        }

        return log.Id;
    }

    private static IReadOnlyDictionary<RequestLogContentSlot, string?> BuildContentValues(
        ProxyRequestLogContext context)
    {
        return new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestHeaders] = SerializeForLog(context.RequestHeaders),
            [RequestLogContentSlot.RequestBody] = context.RawRequestBody ?? SerializeForLog(context.Payload),
            [RequestLogContentSlot.UpstreamRequestBody] = SerializeForLog(context.UpstreamRequest),
            [RequestLogContentSlot.UpstreamResponseBody] = SerializeForLog(context.UpstreamResponse),
            [RequestLogContentSlot.ResponseBody] = SerializeForLog(context.ResponsePayload ?? context.ErrorResponse),
            [RequestLogContentSlot.WebSearchJson] = context.WebSearchDetails is null
                ? null
                : SerializeForLog(context.WebSearchDetails),
            [RequestLogContentSlot.OcrJson] = context.OcrDetails is null
                ? null
                : SerializeForLog(context.OcrDetails),
            [RequestLogContentSlot.StreamLinesJson] = SerializeStreamLines(context.StreamLines)
        };
    }

    private void PublishLogWritten(Guid logId, string ownerUsername, int statusCode, string? error)
    {
        var isError = !string.IsNullOrEmpty(error) || statusCode >= 400;
        _eventBus?.Publish(new RequestLogWrittenEvent
        {
            OwnerUsername = ownerUsername,
            LogId = logId,
            IsError = isError


        });
    }

    private static string? SerializeStreamLines(
        IReadOnlyList<ProxyRequestStreamLineCapture>? streamLines)
    {
        if (streamLines is null)
        {
            return null;
        }

        var values = streamLines
            .OrderBy(item => item.Sequence)
            .Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sequence"] = item.Sequence,
                ["source"] = item.Source,
                ["raw_line"] = item.RawLine
            })
            .ToList();
        return JsonSerializer.Serialize(values, JsonOptions);
    }

    private static void ApplyConversationMetadata(
        RequestLog log,
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, object?>? payload)
    {
        var turnMetadata = ParseTurnMetadata(HeaderValue(requestHeaders, "x-codex-turn-metadata"));
        var threadId = MetadataValue(turnMetadata, "thread_id")
            ?? HeaderValue(requestHeaders, "thread-id");
        var sessionId = MetadataValue(turnMetadata, "session_id")
            ?? HeaderValue(requestHeaders, "session-id")
            ?? HeaderValue(requestHeaders, "x-claude-code-session-id");
        var promptCacheKey = payload is null
            ? null
            : NullIfEmpty(JsonDictionaryValue.String(payload, "prompt_cache_key"));
        log.ConversationKey = threadId is not null
            ? $"thread:{threadId}"
            : sessionId is not null
                ? $"session:{sessionId}"
                : promptCacheKey is not null
                    ? $"prompt_cache_key:{promptCacheKey}"
                    : null;
        log.ConversationTurnId = MetadataValue(turnMetadata, "turn_id")
            ?? HeaderValue(requestHeaders, "x-client-request-id");
        log.ConversationWindowId = MetadataValue(turnMetadata, "window_id")
            ?? HeaderValue(requestHeaders, "x-codex-window-id");
        log.PreviousResponseId = payload is null
            ? null
            : NullIfEmpty(JsonDictionaryValue.String(payload, "previous_response_id"));
    }

    private static void ApplyConversationMetadataFromSerializedRequest(
        RequestLog log,
        string requestHeaders,
        string requestBody)
    {
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(requestHeaders)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(requestBody);
            ApplyConversationMetadata(log, headers, payload);
        }
        catch (JsonException)
        {
            // 日志正文仍会完整保存；不可解析的元数据只是不建立检索索引。
        }
    }

    private static Dictionary<string, string> ParseTurnMetadata(string? value)
    {
        if (value is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? MetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return metadata.TryGetValue(key, out var value) ? NullIfEmpty(value) : null;
    }

    private static string? HeaderValue(
        IReadOnlyDictionary<string, string> headers,
        string key)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return NullIfEmpty(pair.Value);
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string? UpdateOcrJsonParentRequestLogId(string? ocrJson, Guid parentRequestLogId)
    {
        if (string.IsNullOrWhiteSpace(ocrJson))
        {
            return ocrJson;
        }

        try
        {
            using var document = JsonDocument.Parse(ocrJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ocrJson;
            }

            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                dictionary[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(property.Value.GetRawText()),
                    JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(property.Value.GetRawText()),
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetInt64(out var longValue) => longValue,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
            }

            dictionary["parent_request_log_id"] = parentRequestLogId;
            return JsonSerializer.Serialize(dictionary, JsonOptions);
        }
        catch (JsonException)
        {
            return ocrJson;
        }
    }

    private static int CachedTokensFromNestedDetails(
        IReadOnlyDictionary<string, object?> usage,
        string detailsKey)
    {
        return TryAsObject(JsonDictionaryValue.Get(usage, detailsKey), out var details)
            ? ToInt(JsonDictionaryValue.Get(details, "cached_tokens"))
            : 0;
    }

    private static int ChatCachedTokens(IReadOnlyDictionary<string, object?> usage)
    {
        if (TryAsObject(JsonDictionaryValue.Get(usage, "prompt_tokens_details"), out var promptDetails)
            && promptDetails.Count > 0)
        {
            return ToInt(JsonDictionaryValue.Get(promptDetails, "cached_tokens"));
        }

        return TryAsObject(JsonDictionaryValue.Get(usage, "input_tokens_details"), out var inputDetails)
            ? ToInt(JsonDictionaryValue.Get(inputDetails, "cached_tokens"))
            : 0;
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

        if (value is IDictionary<string, object?> genericDictionary)
        {
            dictionary = genericDictionary.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            return true;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is string key)
                {
                    dictionary[key] = entry.Value;
                }
            }

            return true;
        }

        dictionary = [];
        return false;
    }

    private static int ToInt(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value is JsonElement element
                ? element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt32(out var parsed) => parsed,
                    JsonValueKind.String when int.TryParse(
                        element.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed) => parsed,
                    _ => 0
                }
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static string NormalizeUsername(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private Guid ResolveOwnerUserId(string ownerUsername)
    {
        var normalized = NormalizeUsername(ownerUsername);
        if (normalized.Length == 0)
        {
            normalized = "admin";
        }
        var user = _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalized);
        return user?.Id ?? Guid.Empty;
    }

    private static Guid? ParseChannelId(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }
        return Guid.TryParse(channelId, out var parsed) ? parsed : null;
    }

    private static string DefaultOwnerUsername(OpenCodexRuntimeSettings settings)
    {
        var defaultOwnerUsername = NormalizeUsername(settings.AdminUsername);
        return defaultOwnerUsername.Length == 0 ? "admin" : defaultOwnerUsername;
    }

    private static string DetermineLifecycleStatus(int? statusCode, string? error)
    {
        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error)
            ? ProxyRequestLifecycleStatus.Failed
            : ProxyRequestLifecycleStatus.Success;
    }

    private static string SerializeForLog(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        return JsonSerializer.Serialize(value, JsonOptions);
    }
}
