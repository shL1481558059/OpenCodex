using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 观测数据查询实现：把筛选条件转成过滤器字典后转发给底层观测服务。
/// </summary>
public sealed class ObservabilityQueryService : IObservabilityQueryService
{
    private readonly IObservabilityService _observability;

    public ObservabilityQueryService(IObservabilityService observability)
    {
        _observability = observability;
    }

    public ApiOpResult<LogsPageResponse> ReadLogsPage(
        string page,
        string pageSize,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadLogsPage(page, pageSize, filters);
    }

    public ApiOpResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        string q,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: field);
        return _observability.ReadLogFilterOption(field, q, filters);
    }

    public ApiOpResult<LogDetailResponse> ReadLogById(Guid logId)
    {
        return _observability.ReadLogById(logId);
    }

    public ApiOpResult<ActiveChannelQueueResponse> ReadActiveChannelQueue()
    {
        return _observability.ReadActiveChannelQueue();
    }

    public ApiOpResult<IReadOnlyList<RecentErrorItemResponse>> ReadRecentErrors(int limit)
    {
        return _observability.ReadRecentErrors(limit);
    }

    public ApiOpResult<ClearLogsResponse> ClearLogs()
    {
        return _observability.ClearLogs();
    }

    public ApiOpResult<StatsResponse> ReadStats(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadStats(range, start, end, filters);
    }

    public ApiOpResult<StatsSummaryResponse> ReadStatsSummary(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadStatsSummary(range, start, end, filters);
    }

    public ApiOpResult<IReadOnlyList<StatsPointResponse>> ReadStatsTimeseries(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadStatsTimeseries(range, start, end, filters);
    }

    public ApiOpResult<IReadOnlyList<StatsModelDistributionResponse>> ReadStatsModelDistribution(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadStatsModelDistribution(range, start, end, filters);
    }

    public ApiOpResult<IReadOnlyList<ErrorDistributionResponse>> ReadStatsErrorDistribution(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria)
    {
        var filters = BuildLogFilters(criteria, excludedKey: null);
        return _observability.ReadStatsErrorDistribution(range, start, end, filters);
    }

    private static Dictionary<string, object?> BuildLogFilters(
        LogFilterCriteria criteria,
        string? excludedKey)
    {
        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddFilter(filters, "request_id", criteria.RequestId, excludedKey);
        AddFilter(filters, "model", criteria.Model, excludedKey);
        AddFilter(filters, "upstream_model", criteria.UpstreamModel, excludedKey);
        AddFilter(filters, "channel_id", criteria.ChannelId, excludedKey);
        AddFilter(filters, "owner_username", criteria.OwnerUsername, excludedKey);
        AddFilter(filters, "api_key_id", criteria.ApiKeyId, excludedKey);
        AddFilter(filters, "path", criteria.Path, excludedKey);
        AddFilter(filters, "request_type", criteria.RequestType, excludedKey);
        AddFilter(filters, "status_code", criteria.StatusCode, excludedKey);
        AddFilter(filters, "is_stream", criteria.IsStream, excludedKey);
        AddFilter(filters, "client_ip", criteria.ClientIp, excludedKey);
        AddFilter(filters, "error", criteria.Error, excludedKey);
        AddFilter(filters, "request_status", criteria.RequestStatus, excludedKey);
        AddFilter(filters, "created_from", criteria.CreatedFrom, excludedKey);
        AddFilter(filters, "created_to", criteria.CreatedTo, excludedKey);
        AddFilter(filters, "conversation_key", criteria.ConversationKey, excludedKey);
        AddFilter(filters, "conversation_turn_id", criteria.ConversationTurnId, excludedKey);
        AddFilter(filters, "conversation_window_id", criteria.ConversationWindowId, excludedKey);
        AddFilter(filters, "previous_response_id", criteria.PreviousResponseId, excludedKey);
        AddFilter(filters, "parent_request_log_id", criteria.ParentRequestLogId, excludedKey);
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
