using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ObservabilityController : AuthenticatedApiControllerBase
{
    private readonly IObservabilityService _observability;
    private readonly IEventBus _eventBus;

    public ObservabilityController(
        IWorkContext workContext,
        IObservabilityService observability,
        IEventBus eventBus)
        : base(workContext)
    {
        _observability = observability;
        _eventBus = eventBus;
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
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null,
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
            created_to,
            conversation_key,
            conversation_turn_id,
            conversation_window_id,
            previous_response_id);
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
        string? created_to = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
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
            conversation_key,
            conversation_turn_id,
            conversation_window_id,
            previous_response_id,
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
        string? request_status = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
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
            null,
            conversation_key,
            conversation_turn_id,
            conversation_window_id,
            previous_response_id);
        var result = _observability.ReadStats(
            range,
            start,
            end,
            filters);
        return Api(result);
    }




    [HttpGet("/stats/summary")]
    public IActionResult StatsSummary(
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
        string? request_status = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id, model, upstream_model, channel_id, owner_username, api_key_id, path,
            request_type, status_code, is_stream, client_ip, error, request_status, null, null,
            conversation_key, conversation_turn_id, conversation_window_id, previous_response_id);
        var result = _observability.ReadStatsSummary(range, start, end, filters);
        return Api(result);
    }

    [HttpGet("/stats/timeseries")]
    public IActionResult StatsTimeseries(
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
        string? request_status = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id, model, upstream_model, channel_id, owner_username, api_key_id, path,
            request_type, status_code, is_stream, client_ip, error, request_status, null, null,
            conversation_key, conversation_turn_id, conversation_window_id, previous_response_id);
        var result = _observability.ReadStatsTimeseries(range, start, end, filters);
        return Api(result);
    }

    [HttpGet("/stats/model-distribution")]
    public IActionResult StatsModelDistribution(
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
        string? request_status = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id, model, upstream_model, channel_id, owner_username, api_key_id, path,
            request_type, status_code, is_stream, client_ip, error, request_status, null, null,
            conversation_key, conversation_turn_id, conversation_window_id, previous_response_id);
        var result = _observability.ReadStatsModelDistribution(range, start, end, filters);
        return Api(result);
    }

    [HttpGet("/stats/error-distribution")]
    public IActionResult StatsErrorDistribution(
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
        string? request_status = null,
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null)
    {
        RequireUser();
        var filters = BuildLogFilters(
            request_id, model, upstream_model, channel_id, owner_username, api_key_id, path,
            request_type, status_code, is_stream, client_ip, error, request_status, null, null,
            conversation_key, conversation_turn_id, conversation_window_id, previous_response_id);
        var result = _observability.ReadStatsErrorDistribution(range, start, end, filters);
        return Api(result);
    }

    [HttpGet("/monitor/active-channels")]
    public IActionResult MonitorActiveChannels()
    {
        RequireUser();
        var result = _observability.ReadActiveChannelQueue();
        return Api(result);
    }

    [HttpGet("/monitor/recent-errors")]
    public IActionResult MonitorRecentErrors()
    {
        RequireUser();
        var result = _observability.ReadRecentErrors(5);
        return Api(result);
    }

    [HttpGet("/monitor/active-channels/stream")]
    public async Task MonitorActiveChannelsStream()
    {
        RequireUser();
        await WriteActiveChannelStream();
    }

    [HttpGet("/monitor/recent-errors/stream")]
    public async Task MonitorRecentErrorsStream()
    {
        RequireUser();
        await WriteRecentErrorsStream();
    }
    private async Task WriteActiveChannelStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<ChannelCapacityChangedEvent>(
            e => isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                var result = _observability.ReadActiveChannelQueue();
                var payload = result.Payload ?? new ActiveChannelQueueResponse(string.Empty, []);
                var data = JsonSerializer.Serialize(payload);
                await Response.WriteAsync($"event: queue\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
    }

    private async Task WriteRecentErrorsStream()
    {
        var user = RequireUser();
        var isSuperadmin = user.Role == "superadmin";
        var username = user.Username;

        ProxyStreamResponseWriter.PrepareSse(Response);

        var reader = _eventBus.Subscribe<RequestLogWrittenEvent>(
            e => e.IsError && (isSuperadmin || string.Equals(e.OwnerUsername, username, StringComparison.Ordinal)),
            HttpContext.RequestAborted);

        await SseEventWriter.StreamAsync(
            Response,
            reader,
            async ct =>
            {
                var result = _observability.ReadRecentErrors(5);
                var payload = result.Payload ?? [];
                var data = JsonSerializer.Serialize(payload);
                await Response.WriteAsync($"event: errors\n", ct);
                await Response.WriteAsync($"data: {data}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            },
            HttpContext.RequestAborted);
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
        string? conversationKey,
        string? conversationTurnId,
        string? conversationWindowId,
        string? previousResponseId,
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
        AddFilter(filters, "conversation_key", conversationKey, excludedKey);
        AddFilter(filters, "conversation_turn_id", conversationTurnId, excludedKey);
        AddFilter(filters, "conversation_window_id", conversationWindowId, excludedKey);
        AddFilter(filters, "previous_response_id", previousResponseId, excludedKey);
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
