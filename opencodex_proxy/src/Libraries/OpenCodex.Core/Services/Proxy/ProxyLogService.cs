using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
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

    public ProxyLogService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IModelPricingService pricing)
    {
        _settingsProvider = settingsProvider;
        _pricing = pricing;
    }

    public long WriteLog(ProxyLogContext context, ProxyRequestMetadata request)
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
            context.StreamWriteMetrics));
    }

    public long WriteLog(ProxyRequestLogContext context)
    {
        var responseForUsage = context.UpstreamResponse ?? [];
        var usage = context.ChannelType is null
            ? new UsageDto(0, 0, 0)
            : ExtractUsage(responseForUsage, context.ChannelType);
        var responseModel = JsonDictionaryValue.String(responseForUsage, "model");
        if (responseModel.Length == 0)
        {
            responseModel = context.UpstreamModel ?? context.RequestModel ?? string.Empty;
        }

        var settings = _settingsProvider.GetSettings();
        return WriteRequestLog(
            settings,
            new RequestLogWriteDto(
                context.RequestId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
                context.ChannelId,
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
                context.OwnerUsername,
                context.ApiKeyId,
                context.Error,
                context.OcrDetails is null ? null : JsonSerializer.Serialize(context.OcrDetails, JsonOptions),
                context.StreamWriteMetrics is { HasValues: true }
                    ? JsonSerializer.Serialize(context.StreamWriteMetrics, JsonOptions)
                    : null));
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
                ToInt(JsonDictionaryValue.Get(usageObject, "input_tokens")),
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

    private static long WriteRequestLog(
        OpenCodexRuntimeSettings settings,
        RequestLogWriteDto record)
    {
        var defaultOwnerUsername = NormalizeUsername(settings.AdminUsername);
        if (defaultOwnerUsername.Length == 0)
        {
            defaultOwnerUsername = "admin";
        }

        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        OpenCodexRequestLogs.EnsureSchema(context);
        using var transaction = context.Database.BeginTransaction();
        var log = new RequestLog
        {
            RequestId = record.RequestId,
            CreatedAt = record.CreatedAt,
            Method = record.Method,
            Path = record.Path,
            ClientIp = record.ClientIp,
            Model = record.Model,
            UpstreamModel = record.UpstreamModel,
            ChannelId = record.ChannelId,
            RequestType = record.RequestType,
            ParentRequestLogId = record.ParentRequestLogId,
            IsStream = record.IsStream,
            TtftMs = record.TtftMs,
            DurationMs = record.DurationMs,
            StatusCode = record.StatusCode,
            InputTokens = record.InputTokens,
            CachedTokens = record.CachedTokens,
            OutputTokens = record.OutputTokens,
            Cost = record.Cost,
            OwnerUsername = record.OwnerUsername.Length == 0 ? defaultOwnerUsername : record.OwnerUsername,
            ApiKeyId = record.ApiKeyId,
            Error = record.Error,
            Detail = new RequestLogDetail
            {
                RequestHeaders = record.RequestHeaders,
                RequestBody = record.RequestBody,
                UpstreamRequestBody = record.UpstreamRequestBody,
                UpstreamResponseBody = record.UpstreamResponseBody,
                ResponseBody = record.ResponseBody,
                WebSearchJson = record.WebSearchJson,
                OcrJson = record.OcrJson,
                StreamTimingsJson = record.StreamTimingsJson
            }
        };
        context.RequestLogs.Add(log);
        context.SaveChanges();
        if (record.RequestType == ProxyRequestTypes.Main)
        {
            var childLogs = context.RequestLogs
                .Include(item => item.Detail)
                .Where(item => item.RequestType == ProxyRequestTypes.Ocr
                    && item.RequestId == record.RequestId
                    && item.ParentRequestLogId == null)
                .ToList();
            foreach (var child in childLogs)
            {
                child.ParentRequestLogId = log.Id;
                if (child.Detail is not null)
                {
                    child.Detail.OcrJson = UpdateOcrJsonParentRequestLogId(child.Detail.OcrJson, log.Id);
                }
            }

            if (childLogs.Count > 0)
            {
                context.SaveChanges();
            }
        }

        transaction.Commit();
        return log.Id;
    }

    private static string? UpdateOcrJsonParentRequestLogId(string? ocrJson, long parentRequestLogId)
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
}
