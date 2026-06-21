using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义后台观测数据服务。
/// </summary>
public interface IObservabilityService
{
    /// <summary>
    /// 读取请求日志分页数据。
    /// </summary>
    /// <param name="page">页码请求值。</param>
    /// <param name="pageSize">每页数量请求值。</param>
    /// <param name="filters">日志筛选条件。</param>
    /// <returns>请求日志分页结果。</returns>
    ApiOpResult<LogsPageResponse> ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters);

    /// <summary>
    /// 读取指定日志筛选字段的可选值。
    /// </summary>
    /// <param name="field">筛选字段名称。</param>
    /// <param name="query">筛选值查询内容。</param>
    /// <param name="filters">已有筛选条件。</param>
    /// <returns>筛选字段可选值结果。</returns>
    ApiOpResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters);

    /// <summary>
    /// 读取指定日志详情。
    /// </summary>
    /// <param name="logId">日志标识。</param>
    /// <returns>日志详情结果。</returns>
   ApiOpResult<LogDetailResponse> ReadLogById(
        Guid logId);

    /// <summary>
    /// 读取统计数据。
    /// </summary>
    /// <param name="rangeKey">统计范围键。</param>
    /// <param name="startTs">自定义开始时间戳。</param>
    /// <param name="endTs">自定义结束时间戳。</param>
    /// <param name="filters">日志筛选条件。</param>
    /// <returns>统计数据结果。</returns>
    ApiOpResult<StatsResponse> ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters);
}
