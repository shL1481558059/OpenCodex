using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Services;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ObservabilityController : AuthenticatedApiControllerBase
{
    private readonly IObservabilityQueryService _observability;

    public ObservabilityController(
        IWorkContext workContext,
        IObservabilityQueryService observability)
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
        string? conversation_key = null,
        string? conversation_turn_id = null,
        string? conversation_window_id = null,
        string? previous_response_id = null,
        string? parent_request_log_id = null,
        string page = "1",
        string page_size = "50")
    {
        RequireUser();
        return Api(_observability.ReadLogsPage(
            page,
            page_size,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                CreatedFrom = created_from,
                CreatedTo = created_to,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id,
                ParentRequestLogId = parent_request_log_id
            }));
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
        return Api(_observability.ReadLogFilterOption(
            field,
            q,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                CreatedFrom = created_from,
                CreatedTo = created_to,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
    }

    [HttpGet("/logs/{logId:guid}")]
    public IActionResult LogDetail(Guid logId)
    {
        RequireUser();
        return Api(_observability.ReadLogById(logId));
    }

    [HttpDelete("/logs")]
    public IActionResult ClearLogs()
    {
        RequireSuperadmin();
        return Api(_observability.ClearLogs());
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
        return Api(_observability.ReadStats(
            range,
            start,
            end,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
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
        return Api(_observability.ReadStatsSummary(
            range,
            start,
            end,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
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
        return Api(_observability.ReadStatsTimeseries(
            range,
            start,
            end,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
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
        return Api(_observability.ReadStatsModelDistribution(
            range,
            start,
            end,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
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
        return Api(_observability.ReadStatsErrorDistribution(
            range,
            start,
            end,
            new LogFilterCriteria
            {
                RequestId = request_id,
                Model = model,
                UpstreamModel = upstream_model,
                ChannelId = channel_id,
                OwnerUsername = owner_username,
                ApiKeyId = api_key_id,
                Path = path,
                RequestType = request_type,
                StatusCode = status_code,
                IsStream = is_stream,
                ClientIp = client_ip,
                Error = error,
                RequestStatus = request_status,
                ConversationKey = conversation_key,
                ConversationTurnId = conversation_turn_id,
                ConversationWindowId = conversation_window_id,
                PreviousResponseId = previous_response_id
            }));
    }

    [HttpGet("/monitor/active-channels")]
    public IActionResult MonitorActiveChannels()
    {
        RequireUser();
        return Api(_observability.ReadActiveChannelQueue());
    }

    [HttpGet("/monitor/recent-errors")]
    public IActionResult MonitorRecentErrors()
    {
        RequireUser();
        return Api(_observability.ReadRecentErrors(5));
    }
}
