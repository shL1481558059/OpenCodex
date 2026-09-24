using System.Collections;
using System.Globalization;
using System.Text;
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
    private readonly Dictionary<string, Guid> _ownerUserIdCache = new(StringComparer.Ordinal);

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
            [RequestLogContentSlot.RequestBody] = RawBodyText(context.RawRequestBody, context.Payload)
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
        // 本方法只负责以下列,避免全实体 UPDATE 覆盖并发写入的其它字段。
        _logRepository.Update(
            log,
            nameof(RequestLog.OwnerUserId),
            nameof(RequestLog.ApiKeyId),
            nameof(RequestLog.Model),
            nameof(RequestLog.UpstreamModel),
            nameof(RequestLog.ChannelId),
            nameof(RequestLog.IsStream),
            nameof(RequestLog.LifecycleStatus),
            nameof(RequestLog.ProcessingStartedAt));

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
            request.RawBody)
        {
            AggregatedUsage = context.AggregatedUsage
        });
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
            request.RawBody)
        {
            AggregatedUsage = context.AggregatedUsage
        });
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
        var usage = ExtractUsage(context);

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
        // 本方法负责落账与计费相关的全部列,不含 MarkProcessing 写入的
        // ProcessingStartedAt(避免并发时把处理中的时间戳覆盖掉)。
        _logRepository.Update(
            log,
            nameof(RequestLog.OwnerUserId),
            nameof(RequestLog.ApiKeyId),
            nameof(RequestLog.Model),
            nameof(RequestLog.UpstreamModel),
            nameof(RequestLog.ChannelId),
            nameof(RequestLog.RequestType),
            nameof(RequestLog.ParentRequestLogId),
            nameof(RequestLog.IsStream),
            nameof(RequestLog.TtftMs),
            nameof(RequestLog.DurationMs),
            nameof(RequestLog.StatusCode),
            nameof(RequestLog.InputTokens),
            nameof(RequestLog.CachedTokens),
            nameof(RequestLog.CacheWriteTokens),
            nameof(RequestLog.CacheReadTokens),
            nameof(RequestLog.OutputTokens),
            nameof(RequestLog.Cost),
            nameof(RequestLog.CostCurrency),
            nameof(RequestLog.PricingModelInfoId),
            nameof(RequestLog.PricingPlanId),
            nameof(RequestLog.PricingSnapshotJson),
            nameof(RequestLog.Error),
            nameof(RequestLog.LifecycleStatus),
            nameof(RequestLog.CompletedAt),
            nameof(RequestLog.ConversationKey),
            nameof(RequestLog.ConversationTurnId),
            nameof(RequestLog.ConversationWindowId),
            nameof(RequestLog.PreviousResponseId));

        _contentStore.WriteSequential(
            requestLogId,
            ContentSlots,
            slot => EncodeContentSlot(context, slot));

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
                    // OCR 子日志认领只负责 ParentRequestLogId 一列。
                    _logRepository.Update(child, nameof(RequestLog.ParentRequestLogId));
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
        var usage = ExtractUsage(context);

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
                RawBodyText(context.RawRequestBody, context.Payload),
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
                context.OcrDetails is null ? null : SerializeForLog(context.OcrDetails)));

        PublishLogWritten(logId, ownerUsername, context.StatusCode, context.Error);
        return logId;
    }

    private static UsageDto ExtractUsage(ProxyRequestLogContext context)
    {
        var response = context.AggregatedUsage is null
            ? context.UpstreamResponse ?? []
            : new Dictionary<string, object?> { ["usage"] = context.AggregatedUsage };
        return context.ChannelType is null ? new UsageDto(0, 0, 0) : ExtractUsage(response, context.ChannelType);
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

        _contentStore.WriteUtf8(log.Id, new Dictionary<RequestLogContentSlot, byte[]?>
        {
            [RequestLogContentSlot.RequestHeaders] = EncodeText(record.RequestHeaders),
            [RequestLogContentSlot.RequestBody] = EncodeText(record.RequestBody),
            [RequestLogContentSlot.UpstreamRequestBody] = EncodeText(record.UpstreamRequestBody),
            [RequestLogContentSlot.UpstreamResponseBody] = EncodeText(record.UpstreamResponseBody),
            [RequestLogContentSlot.ResponseBody] = EncodeText(record.ResponseBody),
            [RequestLogContentSlot.WebSearchJson] = EncodeText(record.WebSearchJson),
            [RequestLogContentSlot.OcrJson] = EncodeText(record.OcrJson)
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
                    // OCR 子日志认领只负责 ParentRequestLogId 一列。
                    _logRepository.Update(child, nameof(RequestLog.ParentRequestLogId));
                }
            }
        }

        return log.Id;
    }

    /// <summary>
    /// 完成日志时按顺序写入的正文槽位；顺序固定，便于按需逐槽位释放内存。
    /// </summary>
    private static readonly RequestLogContentSlot[] ContentSlots =
    [
        RequestLogContentSlot.RequestHeaders,
        RequestLogContentSlot.RequestBody,
        RequestLogContentSlot.UpstreamRequestBody,
        RequestLogContentSlot.UpstreamResponseBody,
        RequestLogContentSlot.ResponseBody,
        RequestLogContentSlot.WebSearchJson,
        RequestLogContentSlot.OcrJson
    ];

    private static byte[]? EncodeContentSlot(ProxyRequestLogContext context, RequestLogContentSlot slot)
    {
        return slot switch
        {
            RequestLogContentSlot.RequestHeaders => EncodeForLog(context.RequestHeaders),
            RequestLogContentSlot.RequestBody => context.RawRequestBody is { Length: > 0 } rawRequestBody
                ? rawRequestBody.ToArray()
                : EncodeForLog(context.Payload),
            RequestLogContentSlot.UpstreamRequestBody => EncodeForLog(context.UpstreamRequest),
            RequestLogContentSlot.UpstreamResponseBody => EncodeForLog(context.UpstreamResponse),
            RequestLogContentSlot.ResponseBody => EncodeForLog(context.ResponsePayload ?? context.ErrorResponse),
            RequestLogContentSlot.WebSearchJson => context.WebSearchDetails is null
                ? null
                : EncodeForLog(context.WebSearchDetails),
            RequestLogContentSlot.OcrJson => context.OcrDetails is null
                ? null
                : EncodeForLog(context.OcrDetails),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "unsupported log content slot")
        };
    }

    /// <summary>
    /// 把入口捕获的 UTF-8 原文字节还原为文本；没有原文时退回序列化后的载荷。
    /// </summary>
    private static string RawBodyText(ReadOnlyMemory<byte>? rawRequestBody, Dictionary<string, object?>? payload)
    {
        return rawRequestBody is { Length: > 0 } raw
            ? Encoding.UTF8.GetString(raw.Span)
            : SerializeForLog(payload);
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

    private static byte[] EncodeForLog(object? value)
    {
        return Encoding.UTF8.GetBytes(SerializeForLog(value));
    }

    private static byte[]? EncodeText(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static void ApplyConversationMetadata(
        RequestLog log,
        IReadOnlyDictionary<string, string> requestHeaders,
        IReadOnlyDictionary<string, object?>? payload)
    {
        var identity = ProxyConversationIdentity.From(requestHeaders, payload);
        log.ConversationKey = identity.ConversationKey;
        log.ConversationTurnId = identity.TurnId;
        log.ConversationWindowId = identity.WindowId;
        log.PreviousResponseId = identity.PreviousResponseId;
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

        if (_ownerUserIdCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var id = _userRepository.TableNoTracking
            .Where(u => u.Username == normalized)
            .Select(u => u.Id)
            .FirstOrDefault();
        // 不缓存 Guid.Empty：用户名解析不到时可能是用户尚未创建，
        // 缓存空值会让同一实例内后续同名新用户永远映射为空。
        if (id != Guid.Empty)
        {
            _ownerUserIdCache[normalized] = id;
        }

        return id;
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
