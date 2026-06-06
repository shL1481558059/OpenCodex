using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Services;

public sealed class ProxyLogService : IProxyLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IProxyLogRepository _logs;

    public ProxyLogService(IProxyLogRepository logs)
    {
        _logs = logs;
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
            ? new UsageRecord(0, 0, 0)
            : _logs.ExtractUsage(responseForUsage, context.ChannelType);
        var responseModel = JsonDictionaryValue.String(responseForUsage, "model");
        if (responseModel.Length == 0)
        {
            responseModel = context.UpstreamModel ?? context.RequestModel ?? string.Empty;
        }

        return _logs.WriteRequestLog(new RequestLogWriteRecord(
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
            _logs.CalculateCost(
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
}
