using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class AdminObservabilityRepository : IAdminObservabilityRepository
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public AdminObservabilityRepository(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public RequestLogPageRecord ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters)
    {
        return OpenCodexDatabase.ReadLogsPage(
            _settingsProvider.GetSettings().DbPath,
            page,
            pageSize,
            filters);
    }

    public IReadOnlyDictionary<string, object> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters)
    {
        return OpenCodexDatabase.ReadLogFilterOption(
            _settingsProvider.GetSettings().DbPath,
            field,
            query,
            filters);
    }

    public RequestLogRecord? ReadLogById(
        long logId,
        IReadOnlyDictionary<string, object?> filters)
    {
        return OpenCodexDatabase.ReadLogById(
            _settingsProvider.GetSettings().DbPath,
            logId,
            filters);
    }

    public StatsRecord ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        string? ownerUsername)
    {
        return OpenCodexDatabase.ReadStats(
            _settingsProvider.GetSettings().DbPath,
            rangeKey,
            startTs,
            endTs,
            ownerUsername);
    }
}
