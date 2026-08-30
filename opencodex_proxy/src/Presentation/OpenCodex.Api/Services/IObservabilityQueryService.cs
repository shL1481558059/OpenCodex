using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.DTOs.Observability;

namespace OpenCodex.Api.Services;

/// <summary>
/// 观测数据查询服务：统一把日志/统计过滤参数转换成筛选字典。
/// </summary>
public interface IObservabilityQueryService
{
    ApiOpResult<LogsPageResponse> ReadLogsPage(
        string page,
        string pageSize,
        LogFilterCriteria criteria);

    ApiOpResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        string q,
        LogFilterCriteria criteria);

    ApiOpResult<LogDetailResponse> ReadLogById(Guid logId);

    ApiOpResult<ActiveChannelQueueResponse> ReadActiveChannelQueue();

    ApiOpResult<IReadOnlyList<RecentErrorItemResponse>> ReadRecentErrors(int limit);

    ApiOpResult<ClearLogsResponse> ClearLogs();

    ApiOpResult<StatsResponse> ReadStats(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria);

    ApiOpResult<StatsSummaryResponse> ReadStatsSummary(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria);

    ApiOpResult<IReadOnlyList<StatsPointResponse>> ReadStatsTimeseries(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria);

    ApiOpResult<IReadOnlyList<StatsModelDistributionResponse>> ReadStatsModelDistribution(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria);

    ApiOpResult<IReadOnlyList<ErrorDistributionResponse>> ReadStatsErrorDistribution(
        string range,
        string? start,
        string? end,
        LogFilterCriteria criteria);
}

/// <summary>
/// 日志/统计查询筛选条件。
/// </summary>
public sealed class LogFilterCriteria
{
    public string? RequestId { get; init; }
    public string? Model { get; init; }
    public string? UpstreamModel { get; init; }
    public string? ChannelId { get; init; }
    public string? OwnerUsername { get; init; }
    public string? ApiKeyId { get; init; }
    public string? Path { get; init; }
    public string? RequestType { get; init; }
    public string? StatusCode { get; init; }
    public string? IsStream { get; init; }
    public string? ClientIp { get; init; }
    public string? Error { get; init; }
    public string? RequestStatus { get; init; }
    public string? CreatedFrom { get; init; }
    public string? CreatedTo { get; init; }
    public string? ConversationKey { get; init; }
    public string? ConversationTurnId { get; init; }
    public string? ConversationWindowId { get; init; }
    public string? PreviousResponseId { get; init; }
    public string? ParentRequestLogId { get; init; }
}
