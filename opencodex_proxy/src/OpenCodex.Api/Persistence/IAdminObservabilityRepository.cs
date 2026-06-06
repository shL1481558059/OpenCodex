using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IAdminObservabilityRepository
{
    RequestLogPageRecord ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters);

    IReadOnlyDictionary<string, object> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters);

    RequestLogRecord? ReadLogById(
        long logId,
        IReadOnlyDictionary<string, object?> filters);

    StatsRecord ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        string? ownerUsername);
}
