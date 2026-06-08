using OpenCodex.CoreBase.DTOs.AdminObservability;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminObservabilityService
{
    ApiResult<LogsPageResponse> ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<LogDetailResponse> ReadLogById(
        long logId,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<StatsResponse> ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        string currentUsername,
        bool isSuperadmin);
}
