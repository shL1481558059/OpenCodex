using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyLogService : IProxyLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyLogService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
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
            request.Headers));
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
                JsonDumps(context.RequestHeaders),
                JsonDumps(context.Payload),
                JsonDumps(context.UpstreamRequest),
                JsonDumps(context.UpstreamResponse),
                JsonDumps(context.ResponsePayload ?? context.ErrorResponse),
                context.WebSearchDetails is null ? null : JsonDumps(context.WebSearchDetails),
                context.RequestModel,
                context.UpstreamModel,
                context.ChannelId,
                context.IsStream,
                context.TtftMs,
                context.DurationMs,
                context.StatusCode,
                usage.InputTokens,
                usage.CachedTokens,
                usage.OutputTokens,
                OpenCodexPricing.CalculateCost(
                    responseModel,
                    usage.InputTokens,
                    usage.CachedTokens,
                    usage.OutputTokens),
                context.OwnerUsername,
                context.ApiKeyId,
                context.Error));
    }

    private static string JsonDumps(object? value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static UsageDto ExtractUsage(IReadOnlyDictionary<string, object?> response, string protocol)
    {
        var usage = EfServiceSupport.GetOptionalValue(response, "usage");
        if (!EfServiceSupport.TryAsObject(usage, out var usageObject))
        {
            usageObject = [];
        }

        return protocol switch
        {
            "responses" => new UsageDto(
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "input_tokens")),
                EfServiceSupport.CachedTokensFromNestedDetails(usageObject, "input_tokens_details"),
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "output_tokens"))),
            "messages" => new UsageDto(
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "input_tokens")),
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "cache_creation_input_tokens"))
                    + EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "cache_read_input_tokens")),
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "output_tokens"))),
            "chat" => new UsageDto(
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "prompt_tokens")),
                EfServiceSupport.ChatCachedTokens(usageObject),
                EfServiceSupport.ToInt(EfServiceSupport.GetOptionalValue(usageObject, "completion_tokens"))),
            _ => new UsageDto(0, 0, 0)
        };
    }

    private static long WriteRequestLog(
        OpenCodexRuntimeSettings settings,
        RequestLogWriteDto record)
    {
        var defaultOwnerUsername = EfServiceSupport.NormalizeUsername(settings.AdminUsername);
        if (defaultOwnerUsername.Length == 0)
        {
            defaultOwnerUsername = "admin";
        }

        EfServiceSupport.InitializeDatabase(settings.DbPath, defaultOwnerUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
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
                WebSearchJson = record.WebSearchJson
            }
        };
        context.RequestLogs.Add(log);
        context.SaveChanges();
        transaction.Commit();
        return log.Id;
    }
}
