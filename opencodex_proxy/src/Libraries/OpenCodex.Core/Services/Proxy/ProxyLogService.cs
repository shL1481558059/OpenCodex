using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
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
    private readonly IModelPricingService _pricing;
    private readonly IRepository<RequestLog> _logRepository;
    private readonly IRepository<RequestLogDetail> _detailRepository;
    private readonly IRepository<RequestLogStreamLine> _streamLineRepository;
    private readonly IRepository<User> _userRepository;

    public ProxyLogService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IModelPricingService pricing,
        IRepository<RequestLog> logRepository,
        IRepository<RequestLogDetail> detailRepository,
        IRepository<RequestLogStreamLine> streamLineRepository,
        IRepository<User> userRepository)
    {
        _settingsProvider = settingsProvider;
        _pricing = pricing;
        _logRepository = logRepository;
        _detailRepository = detailRepository;
        _streamLineRepository = streamLineRepository;
        _userRepository = userRepository;
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
        _logRepository.Insert(log);

        _detailRepository.Insert(new RequestLogDetail
        {
            RequestLogId = log.Id,
            RequestHeaders = JsonSerializer.Serialize(context.RequestHeaders, JsonOptions),
            RequestBody = JsonSerializer.Serialize(context.Payload, JsonOptions)
        });
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
        log.ChannelId = ParseChannelId(context.ChannelId);
        log.IsStream = context.IsStream;
        log.LifecycleStatus = ProxyRequestLifecycleStatus.Processing;
        log.ProcessingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _logRepository.Update(log);

        // 手动维护 Detail(禁止导航属性)
        var detail = _detailRepository.Table.FirstOrDefault(d => d.RequestLogId == requestLogId);
        if (detail is null)
        {
            _detailRepository.Insert(new RequestLogDetail
            {
                RequestLogId = requestLogId,
                UpstreamRequestBody = JsonSerializer.Serialize(context.UpstreamRequest, JsonOptions)
            });
        }
        else
        {
            detail.UpstreamRequestBody = JsonSerializer.Serialize(context.UpstreamRequest, JsonOptions);
            _detailRepository.Update(detail);
        }
    }

    public void CompleteLog(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
    {
        CompleteLog(requestLogId, new ProxyRequestLogContext(
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
            context.StreamWriteMetrics,
            context.StreamLines));
    }

    public Guid WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
    {
        return WriteLog(new ProxyRequestLogContext(
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
            context.StreamWriteMetrics,
            context.StreamLines));
    }

    public Guid WriteLog(ProxyRequestLogContext context)
    {
        var settings = _settingsProvider.GetSettings();
        return WriteCompletedLog(settings, context);
    }

    private Guid CompleteLog(Guid requestLogId, ProxyRequestLogContext context)
    {
        var settings = _settingsProvider.GetSettings();
        var responseForUsage = context.UpstreamResponse ?? [];
        var usage = context.ChannelType is null
            ? new UsageDto(0, 0, 0)
            : ExtractUsage(responseForUsage, context.ChannelType);
        var responseModel = JsonDictionaryValue.String(responseForUsage, "model");
        if (responseModel.Length == 0)
        {
            responseModel = context.UpstreamModel ?? context.RequestModel ?? string.Empty;
        }

        var log = _logRepository.Table.FirstOrDefault(item => item.Id == requestLogId);
        if (log is null)
        {
            return WriteCompletedLog(settings, context);
        }

        var ownerUsername = context.OwnerUsername.Length == 0
            ? DefaultOwnerUsername(settings)
            : context.OwnerUsername;
        log.OwnerUserId = ResolveOwnerUserId(ownerUsername);
        log.ApiKeyId = context.ApiKeyId;
        log.Model = context.RequestModel ?? log.Model;
        log.UpstreamModel = context.UpstreamModel;
        log.ChannelId = ParseChannelId(context.ChannelId);
        log.RequestType = context.RequestType;
        log.ParentRequestLogId = context.ParentRequestLogId;
        log.IsStream = context.IsStream;
        log.TtftMs = context.TtftMs;
        log.DurationMs = context.DurationMs;
        log.StatusCode = context.StatusCode;
        log.InputTokens = usage.InputTokens;
        log.CachedTokens = usage.CachedTokens;
        log.OutputTokens = usage.OutputTokens;
        log.Cost = _pricing.CalculateCost(
            responseModel,
            usage.InputTokens,
            usage.CachedTokens,
            usage.OutputTokens);
        log.Error = context.Error;
        log.LifecycleStatus = DetermineLifecycleStatus(context.StatusCode, context.Error);
        log.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _logRepository.Update(log);

        // 手动维护 Detail
        var detail = _detailRepository.Table.FirstOrDefault(d => d.RequestLogId == requestLogId);
        var detailHeaders = JsonSerializer.Serialize(context.RequestHeaders, JsonOptions);
        var detailBody = JsonSerializer.Serialize(context.Payload, JsonOptions);
        var detailUpstreamReq = JsonSerializer.Serialize(context.UpstreamRequest, JsonOptions);
        var detailUpstreamResp = JsonSerializer.Serialize(context.UpstreamResponse, JsonOptions);
        var detailResp = JsonSerializer.Serialize(context.ResponsePayload ?? context.ErrorResponse, JsonOptions);
        var detailWebSearch = context.WebSearchDetails is null ? null : JsonSerializer.Serialize(context.WebSearchDetails, JsonOptions);
        var detailOcr = context.OcrDetails is null ? null : JsonSerializer.Serialize(context.OcrDetails, JsonOptions);
        var detailStreamTimings = context.StreamWriteMetrics is { HasValues: true }
            ? JsonSerializer.Serialize(context.StreamWriteMetrics, JsonOptions)
            : null;
        if (detail is null)
        {
            _detailRepository.Insert(new RequestLogDetail
            {
                RequestLogId = requestLogId,
                RequestHeaders = detailHeaders,
                RequestBody = detailBody,
                UpstreamRequestBody = detailUpstreamReq,
                UpstreamResponseBody = detailUpstreamResp,
                ResponseBody = detailResp,
                WebSearchJson = detailWebSearch,
                OcrJson = detailOcr,
                StreamTimingsJson = detailStreamTimings
            });
        }
        else
        {
            detail.RequestHeaders = detailHeaders;
            detail.RequestBody = detailBody;
            detail.UpstreamRequestBody = detailUpstreamReq;
            detail.UpstreamResponseBody = detailUpstreamResp;
            detail.ResponseBody = detailResp;
            detail.WebSearchJson = detailWebSearch;
            detail.OcrJson = detailOcr;
            detail.StreamTimingsJson = detailStreamTimings;
            _detailRepository.Update(detail);
        }

        // 手动维护 StreamLines(删旧+插新)
        if (context.StreamLines is not null && context.StreamLines.Count > 0)
        {
            var oldLines = _streamLineRepository.Table
                .Where(line => line.RequestLogId == requestLogId)
                .ToList();
            if (oldLines.Count > 0)
            {
                _streamLineRepository.Delete(oldLines);
            }

            var newLines = context.StreamLines
                .OrderBy(item => item.Sequence)
                .Select(item => new RequestLogStreamLine
                {
                    RequestLogId = requestLogId,
                    Sequence = item.Sequence,
                    OccurredAt = item.OccurredAt,
                    Source = item.Source,
                    RawLine = item.RawLine
                })
                .ToList();
            if (newLines.Count > 0)
            {
                _streamLineRepository.Insert(newLines);
            }
        }

        if (context.RequestType == ProxyRequestTypes.Main)
        {
            var childLogs = _logRepository.Table
                .Where(item => item.RequestType == ProxyRequestTypes.Ocr
                    && item.RequestId == context.RequestId
                    && item.ParentRequestLogId == null)
                .ToList();
            if (childLogs.Count > 0)
            {
                var childDetails = _detailRepository.Table
                    .Where(d => childLogs.Select(c => c.Id).Contains(d.RequestLogId))
                    .ToList();
                foreach (var child in childLogs)
                {
                    child.ParentRequestLogId = log.Id;
                    var childDetail = childDetails.FirstOrDefault(d => d.RequestLogId == child.Id);
                    if (childDetail is not null)
                    {
                        childDetail.OcrJson = UpdateOcrJsonParentRequestLogId(childDetail.OcrJson, log.Id);
                    }
                }
                foreach (var child in childLogs)
                {
                    _logRepository.Update(child);
                }
                foreach (var childDetail in childDetails)
                {
                    _detailRepository.Update(childDetail);
                }
            }
        }

        return log.Id;
    }

    private Guid WriteCompletedLog(OpenCodexRuntimeSettings settings, ProxyRequestLogContext context)
    {
        var ownerUsername = context.OwnerUsername.Length == 0
            ? DefaultOwnerUsername(settings)
            : context.OwnerUsername;
        var ownerUserId = ResolveOwnerUserId(ownerUsername);
        var responseForUsage = context.UpstreamResponse ?? [];
        var usage = context.ChannelType is null
            ? new UsageDto(0, 0, 0)
            : ExtractUsage(responseForUsage, context.ChannelType);
        var responseModel = JsonDictionaryValue.String(responseForUsage, "model");
        if (responseModel.Length == 0)
        {
            responseModel = context.UpstreamModel ?? context.RequestModel ?? string.Empty;
        }

        return WriteRequestLog(
            settings,
            new RequestLogWriteDto(
                context.RequestId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                DetermineLifecycleStatus(context.StatusCode, context.Error),
                context.Method,
                context.Path,
                context.ClientIp,
                JsonSerializer.Serialize(context.RequestHeaders, JsonOptions),
                JsonSerializer.Serialize(context.Payload, JsonOptions),
                JsonSerializer.Serialize(context.UpstreamRequest, JsonOptions),
                JsonSerializer.Serialize(context.UpstreamResponse, JsonOptions),
                JsonSerializer.Serialize(context.ResponsePayload ?? context.ErrorResponse, JsonOptions),
                context.WebSearchDetails is null ? null : JsonSerializer.Serialize(context.WebSearchDetails, JsonOptions),
                context.RequestModel,
                context.UpstreamModel,
                ParseChannelId(context.ChannelId),
                context.RequestType,
                context.ParentRequestLogId,
                context.IsStream,
                context.TtftMs,
                context.DurationMs,
                context.StatusCode,
                usage.InputTokens,
                usage.CachedTokens,
                usage.OutputTokens,
                _pricing.CalculateCost(
                    responseModel,
                    usage.InputTokens,
                    usage.CachedTokens,
                    usage.OutputTokens),
                ownerUserId,
                context.ApiKeyId,
                context.Error,
                context.OcrDetails is null ? null : JsonSerializer.Serialize(context.OcrDetails, JsonOptions),
                context.StreamWriteMetrics is { HasValues: true }
                    ? JsonSerializer.Serialize(context.StreamWriteMetrics, JsonOptions)
                    : null,
                context.StreamLines));
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
                ToInt(JsonDictionaryValue.Get(usageObject, "output_tokens"))),
            "messages" => new UsageDto(
                ToInt(JsonDictionaryValue.Get(usageObject, "input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_creation_input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_read_input_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "cache_creation_input_tokens"))
                    + ToInt(JsonDictionaryValue.Get(usageObject, "cache_read_input_tokens")),
                ToInt(JsonDictionaryValue.Get(usageObject, "output_tokens"))),
            "chat" => new UsageDto(
                ToInt(JsonDictionaryValue.Get(usageObject, "prompt_tokens")),
                ChatCachedTokens(usageObject),
                ToInt(JsonDictionaryValue.Get(usageObject, "completion_tokens"))),
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
            OutputTokens = record.OutputTokens,
            Cost = record.Cost,
            OwnerUserId = record.OwnerUserId,
            ApiKeyId = record.ApiKeyId,
            Error = record.Error
        };
        _logRepository.Insert(log);

        _detailRepository.Insert(new RequestLogDetail
        {
            RequestLogId = log.Id,
            RequestHeaders = record.RequestHeaders,
            RequestBody = record.RequestBody,
            UpstreamRequestBody = record.UpstreamRequestBody,
            UpstreamResponseBody = record.UpstreamResponseBody,
            ResponseBody = record.ResponseBody,
            WebSearchJson = record.WebSearchJson,
            OcrJson = record.OcrJson,
            StreamTimingsJson = record.StreamTimingsJson
        });

        if (record.StreamLines is not null && record.StreamLines.Count > 0)
        {
            var streamLines = record.StreamLines
                .OrderBy(item => item.Sequence)
                .Select(item => new RequestLogStreamLine
                {
                    RequestLogId = log.Id,
                    Sequence = item.Sequence,
                    OccurredAt = item.OccurredAt,
                    Source = item.Source,
                    RawLine = item.RawLine
                })
                .ToList();
            _streamLineRepository.Insert(streamLines);
        }

        if (record.RequestType == ProxyRequestTypes.Main)
        {
            var childLogs = _logRepository.Table
                .Where(item => item.RequestType == ProxyRequestTypes.Ocr
                    && item.RequestId == record.RequestId
                    && item.ParentRequestLogId == null)
                .ToList();
            if (childLogs.Count > 0)
            {
                var childDetails = _detailRepository.Table
                    .Where(d => childLogs.Select(c => c.Id).Contains(d.RequestLogId))
                    .ToList();
                foreach (var child in childLogs)
                {
                    child.ParentRequestLogId = log.Id;
                    var childDetail = childDetails.FirstOrDefault(d => d.RequestLogId == child.Id);
                    if (childDetail is not null)
                    {
                        childDetail.OcrJson = UpdateOcrJsonParentRequestLogId(childDetail.OcrJson, log.Id);
                    }
                }
                foreach (var child in childLogs)
                {
                    _logRepository.Update(child);
                }
                foreach (var childDetail in childDetails)
                {
                    _detailRepository.Update(childDetail);
                }
            }
        }

        return log.Id;
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
}
