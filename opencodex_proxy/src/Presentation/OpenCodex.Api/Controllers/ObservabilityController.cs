using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ObservabilityController : AuthenticatedApiControllerBase
{
    private readonly IObservabilityService _observability;

    public ObservabilityController(
        IWorkContext workContext,
        IObservabilityService observability)
        : base(workContext)
    {
        _observability = observability;
    }

    [HttpGet("/logs")]
    public IActionResult Logs(
        string? request_id,
        string? model,
        string? upstream_model,
        string? channel_id,
        string? owner_username,
        string? api_key_id,
        string? path,
        string? request_type,
        string? status_code,
        string? is_stream,
        string? client_ip,
        string? error,
        string? request_status,
        string? created_from,
        string? created_to,
        string page = "1",
        string page_size = "50")
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id,
            model,
            upstream_model,
            channel_id,
            owner_username,
            api_key_id,
            path,
            request_type,
            status_code,
            is_stream,
            client_ip,
            error,
            request_status,
            created_from,
            created_to);
        var result = _observability.ReadLogsPage(
            page,
            page_size,
            filters);
        return Api(result);
    }

    [HttpGet("/log-filter-options")]
    public IActionResult LogFilterOptions(
        string field = "",
        string q = "",
        string? request_id = null,
        string? model = null,
        string? upstream_model = null,
        string? channel_id = null,
        string? owner_username = null,
        string? api_key_id = null,
        string? path = null,
        string? request_type = null,
        string? status_code = null,
        string? is_stream = null,
        string? client_ip = null,
        string? error = null,
        string? request_status = null,
        string? created_from = null,
        string? created_to = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id,
            model,
            upstream_model,
            channel_id,
            owner_username,
            api_key_id,
            path,
            request_type,
            status_code,
            is_stream,
            client_ip,
            error,
            request_status,
            created_from,
            created_to,
            field);
        var result = _observability.ReadLogFilterOption(
            field,
            q,
            filters);
        return Api(result);
    }

    [HttpGet("/logs/{logId:guid}")]
    public IActionResult LogDetail(Guid logId)
    {
        RequireUser();
        var result = _observability.ReadLogById(logId);
        return Api(result);
    }

    [HttpDelete("/logs")]
    public IActionResult ClearLogs()
    {
        RequireSuperadmin();
        var result = _observability.ClearLogs();
        return Api(result);
    }

    [HttpGet("/stats")]
    public IActionResult Stats(
        string range = "1h",
        string? start = null,
        string? end = null,
        string? request_id = null,
        string? model = null,
        string? upstream_model = null,
        string? channel_id = null,
        string? owner_username = null,
        string? api_key_id = null,
        string? path = null,
        string? request_type = null,
        string? status_code = null,
        string? is_stream = null,
        string? client_ip = null,
        string? error = null,
        string? request_status = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id,
            model,
            upstream_model,
            channel_id,
            owner_username,
            api_key_id,
            path,
            request_type,
            status_code,
            is_stream,
            client_ip,
            error,
            request_status,
            null,
            null);
        var result = _observability.ReadStats(
            range,
            start,
            end,
            filters);
        return Api(result);
    }

    [HttpGet("/stats/active-channels")]
    public IActionResult ActiveChannels()
    {
        RequireUser();
        var result = _observability.ReadActiveChannelQueue();
        return Api(result);
    }

    [HttpGet("/stats/active-channels/stream")]
    public async Task ActiveChannelsStream()
    {
        RequireUser();
        ProxyStreamResponseWriter.PrepareSse(Response);

        while (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            var result = _observability.ReadActiveChannelQueue();
            var payload = result.Payload ?? new ActiveChannelQueueResponse(string.Empty, []);
            var data = JsonSerializer.Serialize(payload);

            await Response.WriteAsync($"event: queue\n", HttpContext.RequestAborted);
            await Response.WriteAsync($"data: {data}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), HttpContext.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static Dictionary<string, object?> BuildLogFilters(
        string? requestId,
        string? model,
        string? upstreamModel,
        string? channelId,
        string? ownerUsername,
        string? apiKeyId,
        string? path,
        string? requestType,
        string? statusCode,
        string? isStream,
        string? clientIp,
        string? error,
        string? requestStatus,
        string? createdFrom,
        string? createdTo,
        string? excludedKey = null)
    {
        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddFilter(filters, "request_id", requestId, excludedKey);
        AddFilter(filters, "model", model, excludedKey);
        AddFilter(filters, "upstream_model", upstreamModel, excludedKey);
        AddFilter(filters, "channel_id", channelId, excludedKey);
        AddFilter(filters, "owner_username", ownerUsername, excludedKey);
        AddFilter(filters, "api_key_id", apiKeyId, excludedKey);
        AddFilter(filters, "path", path, excludedKey);
        AddFilter(filters, "request_type", requestType, excludedKey);
        AddFilter(filters, "status_code", statusCode, excludedKey);
        AddFilter(filters, "is_stream", isStream, excludedKey);
        AddFilter(filters, "client_ip", clientIp, excludedKey);
        AddFilter(filters, "error", error, excludedKey);
        AddFilter(filters, "request_status", requestStatus, excludedKey);
        AddFilter(filters, "created_from", createdFrom, excludedKey);
        AddFilter(filters, "created_to", createdTo, excludedKey);
        return filters;
    }

    private static void AddFilter(
        Dictionary<string, object?> filters,
        string key,
        string? value,
        string? excludedKey)
    {
        if (key == excludedKey || string.IsNullOrEmpty(value))
        {
            return;
        }

        filters[key] = value;
    }
}
