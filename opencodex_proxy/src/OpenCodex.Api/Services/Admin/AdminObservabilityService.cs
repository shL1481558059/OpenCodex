using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminObservabilityService : IAdminObservabilityService
{
    private readonly IAdminObservabilityRepository _observability;

    public AdminObservabilityService(IAdminObservabilityRepository observability)
    {
        _observability = observability;
    }

    public ServiceResult<RequestLogPageRecord> ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin)
    {
        return ServiceResult.Success(_observability.ReadLogsPage(
            page,
            pageSize,
            ScopedFilters(filters, currentUsername, isSuperadmin)));
    }

    public ServiceResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin)
    {
        return ServiceResult.Success(_observability.ReadLogFilterOption(
            field,
            query,
            ScopedFilters(filters, currentUsername, isSuperadmin)));
    }

    public ServiceResult<RequestLogRecord> ReadLogById(
        long logId,
        string currentUsername,
        bool isSuperadmin)
    {
        var filters = ScopedFilters(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            currentUsername,
            isSuperadmin);
        var log = _observability.ReadLogById(logId, filters);
        return log is null
            ? ServiceResult.Fail<RequestLogRecord>(AdminObservabilityErrorCodes.NotFound, "log not found")
            : ServiceResult.Success(log);
    }

    public ServiceResult<StatsRecord> ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        string currentUsername,
        bool isSuperadmin)
    {
        return ServiceResult.Success(_observability.ReadStats(
            rangeKey,
            startTs,
            endTs,
            isSuperadmin ? null : currentUsername));
    }

    private static Dictionary<string, object?> ScopedFilters(
        IReadOnlyDictionary<string, object?> filters,
        string currentUsername,
        bool isSuperadmin)
    {
        var scoped = filters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        if (!isSuperadmin)
        {
            scoped["owner_username"] = currentUsername;
        }

        return scoped;
    }
}
