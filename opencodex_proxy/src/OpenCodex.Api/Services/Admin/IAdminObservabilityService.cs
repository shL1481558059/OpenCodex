using OpenCodex.Api.Domain;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public interface IAdminObservabilityService
{
    ServiceResult<RequestLogPageRecord> ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<RequestLogRecord> ReadLogById(
        long logId,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<StatsRecord> ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        string currentUsername,
        bool isSuperadmin);
}
