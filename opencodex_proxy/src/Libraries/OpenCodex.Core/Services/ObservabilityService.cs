using System.Globalization;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Persistence;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

public sealed class ObservabilityService : IObservabilityService
{
    private static readonly HashSet<string> RequestStatusValues = new(StringComparer.Ordinal)
    {
        ProxyRequestLifecycleStatus.Queued,
        ProxyRequestLifecycleStatus.Processing,
        "success",
        "failed"
    };

    private static readonly IReadOnlyDictionary<string, (string OptionKey, string OptionType)> LogFilterFields =
        new Dictionary<string, (string OptionKey, string OptionType)>(StringComparer.Ordinal)
        {
            ["request_id"] = ("request_ids", "text"),
            ["model"] = ("models", "text"),
            ["upstream_model"] = ("upstream_models", "text"),
            ["channel_id"] = ("channel_ids", "text"),
            ["owner_username"] = ("owner_usernames", "text"),
            ["path"] = ("paths", "text"),
            ["request_type"] = ("request_types", "text"),
            ["status_code"] = ("status_codes", "int"),
            ["api_key_id"] = ("api_key_ids", "int")
        };

    private static readonly IReadOnlyDictionary<string, int> StatsRangeGranularity =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1h"] = 1,
            ["6h"] = 5,
            ["24h"] = 15,
            ["7d"] = 120,
            ["30d"] = 720
        };

    private static readonly IReadOnlyDictionary<string, int> StatsRangeHours =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1h"] = 1,
            ["6h"] = 6,
            ["24h"] = 24,
            ["7d"] = 24 * 7,
            ["30d"] = 24 * 30
    };

    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IWorkContext _workContext;
    private readonly IRepository<RequestLog> _logRepository;
    private readonly IRepository<RequestLogDetail> _detailRepository;
    private readonly IRepository<RequestLogStreamLine> _streamLineRepository;
    private readonly IRepository<AccessApiKey> _keyRepository;
    private readonly IRepository<User> _userRepository;
    private readonly IRepository<Channel> _channelRepository;

    public ObservabilityService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IWorkContext workContext,
        IRepository<RequestLog> logRepository,
        IRepository<RequestLogDetail> detailRepository,
        IRepository<RequestLogStreamLine> streamLineRepository,
        IRepository<AccessApiKey> keyRepository,
        IRepository<User> userRepository,
        IRepository<Channel> channelRepository)
    {
        _settingsProvider = settingsProvider;
        _workContext = workContext;
        _logRepository = logRepository;
        _detailRepository = detailRepository;
        _streamLineRepository = streamLineRepository;
        _keyRepository = keyRepository;
        _userRepository = userRepository;
        _channelRepository = channelRepository;
    }

    public ApiOpResult<LogsPageResponse> ReadLogsPage(
        object? page,
        object? pageSize,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var logsPage = QueryLogsPage(
            page,
            pageSize,
            ScopedFilters(filters, currentUsername, isSuperadmin));
        return ApiOpResult<LogsPageResponse>.Succeed(LogsPageResponse.From(
            logsPage,
            ReadApiKeyNames(logsPage.Events.Select(log => log.ApiKeyId)),
            ReadChannelNames(logsPage.Events.Select(log => log.ChannelId))));
    }

    public ApiOpResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(
        string field,
        object? query,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        return ApiOpResult<IReadOnlyDictionary<string, object>>.Succeed(QueryLogFilterOption(
            field,
            query,
            ScopedFilters(filters, currentUsername, isSuperadmin)));
    }

    public ApiOpResult<LogDetailResponse> ReadLogById(
        Guid logId)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var filters = ScopedFilters(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            currentUsername,
            isSuperadmin);
        var log = ReadLogById(logId, filters);
        return log is null
            ? ApiOpResult<LogDetailResponse>.Fail(404, "log not found")
            : ApiOpResult<LogDetailResponse>.Succeed(LogDetailResponse.From(
                log,
                ReadApiKeyNames(new[] { log.ApiKeyId }),
                ReadChannelNames(new[] { log.ChannelId?.ToString() })));
    }

    public ApiOpResult<StatsResponse> ReadStats(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        return ApiOpResult<StatsResponse>.Succeed(StatsResponse.From(QueryStats(
            rangeKey,
            startTs,
            endTs,
            ScopedFilters(filters, currentUsername, isSuperadmin))));
    }

    public ApiOpResult<ClearLogsResponse> ClearLogs()
    {
        var currentUser = _workContext.RequireUser();
        if (currentUser.Role != "superadmin")
        {
            return ApiOpResult<ClearLogsResponse>.Fail(403, "only superadmin can clear logs");
        }

        var logs = _logRepository.Table.ToList();
        var details = _detailRepository.Table.ToList();
        var streamLines = _streamLineRepository.Table.ToList();

        if (streamLines.Count > 0)
        {
            _streamLineRepository.Delete(streamLines);
        }

        if (details.Count > 0)
        {
            _detailRepository.Delete(details);
        }

        if (logs.Count > 0)
        {
            _logRepository.Delete(logs);
        }

        return ApiOpResult<ClearLogsResponse>.Succeed(new ClearLogsResponse(
            logs.Count,
            details.Count,
            streamLines.Count));
    }

    private Dictionary<Guid, string> BuildOwnerMap(IReadOnlyList<RequestLog> logs)
{
    var ownerIds = logs.Select(log => log.OwnerUserId).Distinct().ToList();
    return ownerIds.Count > 0
            ? _userRepository.TableNoTracking
                .Where(u => ownerIds.Contains(u.Id))
                .ToDictionary(u => u.Id, u => u.Username)
            : new Dictionary<Guid, string>();
    }

    private Guid ResolveOwnerUserIdFilter(string username)
    {
        var normalized = (username ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return Guid.Empty;
        }
        return _userRepository.TableNoTracking.FirstOrDefault(u => u.Username == normalized)?.Id ?? Guid.Empty;
    }

    private (string Username, bool IsSuperadmin) CurrentScope()
    {
        var currentUser = _workContext.RequireUser();
        return (currentUser.Username, currentUser.Role == "superadmin");
    }

    private RequestLogPageDto QueryLogsPage(
        object? page = null,
        object? pageSize = null,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        var parsedPageSize = ParseLogPageSize(pageSize);
        var parsedPage = ParseLogPage(page);
        var offset = (parsedPage - 1) * parsedPageSize;
        var query = ApplyLogFilters(_logRepository.TableNoTracking, filters ?? new Dictionary<string, object?>());
        var total = query.Count();
        var logs = query
            .OrderByDescending(log => log.Id)
            .Skip(offset)
            .Take(parsedPageSize)
            .ToList();
        var ownerMap = BuildOwnerMap(logs);
        var events = logs
            .Select(log => MapRequestLogEvent(log, ownerMap.TryGetValue(log.OwnerUserId, out var name) ? name : string.Empty))
            .ToList();

        return new RequestLogPageDto(events, total, parsedPage, parsedPageSize);
    }

    private RequestLogDto? ReadLogById(
        object? logId,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (logId is Guid guidId)
        {
            // 直接用 Guid
        }
        else if (logId is string text && Guid.TryParse(text, out var parsed))
        {
            guidId = parsed;
        }
        else
        {
            return null;
        }

        var query = ApplyLogFilters(_logRepository.TableNoTracking, filters ?? new Dictionary<string, object?>());
        var log = query.FirstOrDefault(item => item.Id == guidId);
        if (log is null)
        {
            return null;
        }

        var detail = _detailRepository.TableNoTracking.FirstOrDefault(d => d.RequestLogId == log.Id);
        var streamLines = _streamLineRepository.TableNoTracking
            .Where(line => line.RequestLogId == log.Id)
            .OrderBy(line => line.Sequence)
            .ToList();
        var ownerUsername = _userRepository.TableNoTracking
            .FirstOrDefault(u => u.Id == log.OwnerUserId)?.Username ?? string.Empty;
        return MapRequestLog(log, detail, streamLines, ownerUsername);
    }

    private IReadOnlyDictionary<string, object> QueryLogFilterOption(
        string field,
        object? query = null,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (field == "request_status")
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request_statuses"] = RequestStatusValues.ToList()
            };
        }

        if (field == "request_type")
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request_types"] = new List<string> { ProxyRequestTypes.Main, ProxyRequestTypes.Ocr }
            };
        }

        if (!LogFilterFields.TryGetValue(field, out var option))
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        var logs = ApplyLogFilters(_logRepository.TableNoTracking, filters ?? new Dictionary<string, object?>());
        var values = field == "api_key_id"
            ? (object)DistinctApiKeyOptions(logs, query)
            : option.OptionType == "int"
            ? (object)DistinctIntValues(logs, field, query)
            : DistinctTextValues(logs, field, query);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [option.OptionKey] = values
        };
    }

    private StatsDto QueryStats(
        string? rangeKey = "1h",
        object? startTs = null,
        object? endTs = null,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        var query = ApplyLogFilters(
            _logRepository.TableNoTracking
            .Where(log => log.CreatedAt >= resolved.StartTs && log.CreatedAt < resolved.EndTs),
            filters ?? new Dictionary<string, object?>());

        var logs = query.ToList();
        var bucketSeconds = resolved.GranularityMinutes * 60.0;
        var bucketCount = Math.Max(
            1,
            (int)Math.Floor((resolved.EndTs - resolved.StartTs + bucketSeconds - 1) / bucketSeconds));
        var points = new List<StatsPointDto>();

        for (var index = 0; index < bucketCount; index++)
        {
            var bucketStart = resolved.StartTs + index * bucketSeconds;
            var bucketEnd = resolved.StartTs + (index + 1) * bucketSeconds;
            var bucketLogs = logs
                .Where(log => log.CreatedAt >= bucketStart && log.CreatedAt < bucketEnd)
                .ToList();
            var cost = bucketLogs.Sum(log => log.Cost);
            var inputTokens = bucketLogs.Sum(log => log.InputTokens);
            var cachedTokens = bucketLogs.Sum(log => log.CachedTokens);
            var outputTokens = bucketLogs.Sum(log => log.OutputTokens);
            var ttftValues = bucketLogs
                .Where(log => log.TtftMs is > 0)
                .Select(log => log.TtftMs!.Value)
                .ToList();
            var avgTtft = ttftValues.Count == 0 ? (double?)null : ttftValues.Average();
            var cacheDenominator = inputTokens + cachedTokens;
            points.Add(new StatsPointDto(
                TimestampToIso(bucketEnd),
                Math.Round(cost, 6),
                inputTokens,
                cachedTokens,
                outputTokens,
                avgTtft is null ? null : Math.Round(avgTtft.Value, 1),
                cacheDenominator > 0 ? Math.Round((double)cachedTokens / cacheDenominator, 4) : null,
                bucketLogs.Count > 0 ? Math.Round((double)bucketLogs.Count / resolved.GranularityMinutes, 2) : 0));
        }

        var modelDistribution = ReadModelDistribution(logs);
        var summary = ReadStatsSummary(
            logs,
            resolved.StartTs,
            resolved.EndTs,
            resolved.GranularityMinutes);

        return new StatsDto(
            resolved.RangeKey,
            TimestampToIso(resolved.StartTs),
            TimestampToIso(resolved.EndTs),
            resolved.GranularityMinutes,
            PricingDefaults.UsdCnyRate,
            summary,
            points,
            modelDistribution);
    }

    private IQueryable<RequestLog> ApplyLogFilters(
        IQueryable<RequestLog> query,
        IReadOnlyDictionary<string, object?> filters)
    {
        foreach (var (field, value) in filters)
        {
            if (IsEmptyLogFilterValue(value))
            {
                continue;
            }

            query = ApplyLogFilter(query, field, value);
        }

        return query;
    }

    private IQueryable<RequestLog> ApplyLogFilter(
        IQueryable<RequestLog> query,
        string field,
        object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return field switch
        {
            "request_id" when text.Length > 0 => query.Where(log => log.RequestId != null && log.RequestId.Contains(text)),
            "model" when text.Length > 0 => query.Where(log => log.Model != null && log.Model.Contains(text)),
            "upstream_model" when text.Length > 0 => query.Where(log => log.UpstreamModel != null && log.UpstreamModel.Contains(text)),
            "channel_id" when text.Length > 0 && Guid.TryParse(text, out var channelId) => query.Where(log => log.ChannelId == channelId),
            "owner_username" when text.Length > 0 => query.Where(log => log.OwnerUserId == ResolveOwnerUserIdFilter(text)),
            "path" when text.Length > 0 => query.Where(log => log.Path != null && log.Path.Contains(text)),
            "request_type" when text.Length > 0 => query.Where(log => log.RequestType == text),
            "client_ip" when text.Length > 0 => query.Where(log => log.ClientIp != null && log.ClientIp.Contains(text)),
            "error" when text.Length > 0 => query.Where(log => log.Error != null && log.Error.Contains(text)),
            "status_code" => ApplyStatusCodeFilter(query, value),
            "is_stream" => ApplyIsStreamFilter(query, value),
            "api_key_id" => ApplyApiKeyIdFilter(query, value),
            "request_status" => ApplyRequestStatusFilter(query, value),
            "created_from" => ApplyCreatedFromFilter(query, value),
            "created_to" => ApplyCreatedToFilter(query, value),
            _ => query
        };
    }

    private static IQueryable<RequestLog> ApplyStatusCodeFilter(IQueryable<RequestLog> query, object? value)
    {
        return TryConvertInt32(value, out var parsed)
            ? query.Where(log => log.StatusCode == parsed)
            : query;
    }

    private static IQueryable<RequestLog> ApplyIsStreamFilter(IQueryable<RequestLog> query, object? value)
    {
        return TryConvertInt64(value, out var parsed)
            ? query.Where(log => log.IsStream == (parsed != 0))
            : query;
    }

    private static IQueryable<RequestLog> ApplyApiKeyIdFilter(IQueryable<RequestLog> query, object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return Guid.TryParse(text, out var parsed)
            ? query.Where(log => log.ApiKeyId == parsed)
            : query;
    }

    private static IQueryable<RequestLog> ApplyRequestStatusFilter(IQueryable<RequestLog> query, object? value)
    {
        var requestStatus = (value?.ToString() ?? string.Empty).Trim();
        if (!RequestStatusValues.Contains(requestStatus))
        {
            return query;
        }

        return requestStatus switch
        {
            ProxyRequestLifecycleStatus.Queued => query.Where(log => log.LifecycleStatus == ProxyRequestLifecycleStatus.Queued),
            ProxyRequestLifecycleStatus.Processing => query.Where(log => log.LifecycleStatus == ProxyRequestLifecycleStatus.Processing),
            ProxyRequestLifecycleStatus.Success => query.Where(log =>
                log.LifecycleStatus == ProxyRequestLifecycleStatus.Success
                || (log.LifecycleStatus == null && log.StatusCode < 400 && string.IsNullOrEmpty(log.Error))),
            ProxyRequestLifecycleStatus.Failed => query.Where(log =>
                log.LifecycleStatus == ProxyRequestLifecycleStatus.Failed
                || (log.LifecycleStatus == null && (log.StatusCode >= 400 || !string.IsNullOrEmpty(log.Error)))),
            _ => query
        };
    }

    private static IQueryable<RequestLog> ApplyCreatedFromFilter(IQueryable<RequestLog> query, object? value)
    {
        return TryConvertDouble(value, out var parsed)
            ? query.Where(log => log.CreatedAt >= parsed)
            : query;
    }

    private static IQueryable<RequestLog> ApplyCreatedToFilter(IQueryable<RequestLog> query, object? value)
    {
        return TryConvertDouble(value, out var parsed)
            ? query.Where(log => log.CreatedAt <= parsed)
            : query;
    }

    private static Dictionary<string, object> EmptyLogFilterOptions()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["request_ids"] = new List<string>(),
            ["models"] = new List<string>(),
            ["upstream_models"] = new List<string>(),
            ["channel_ids"] = new List<string>(),
            ["owner_usernames"] = new List<string>(),
            ["paths"] = new List<string>(),
            ["request_types"] = new List<string> { ProxyRequestTypes.Main, ProxyRequestTypes.Ocr },
            ["status_codes"] = new List<int>(),
            ["api_key_ids"] = new List<LogApiKeyFilterOption>(),
            ["request_statuses"] = RequestStatusValues.ToList()
        };
    }

    private Dictionary<Guid, string> ReadApiKeyNames(
        IEnumerable<Guid?> apiKeyIds)
    {
        var ids = apiKeyIds
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return _keyRepository.TableNoTracking
            .Where(key => ids.Contains(key.Id))
            .Select(key => new { key.Id, key.Name })
            .AsEnumerable()
            .ToDictionary(key => key.Id, key => key.Name);
    }

    private Dictionary<Guid, string> ReadChannelNames(
        IEnumerable<string?> channelIdTexts)
    {
        var ids = channelIdTexts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return _channelRepository.TableNoTracking
            .Where(channel => ids.Contains(channel.Id))
            .Select(channel => new { channel.Id, channel.Name })
            .AsEnumerable()
            .ToDictionary(channel => channel.Id, channel => channel.Name);
    }

    private List<string> DistinctTextValues(
        IQueryable<RequestLog> query,
        string field,
        object? search = null)
    {
        var values = field switch
        {
            "request_id" => query.Select(log => log.RequestId),
            "model" => query.Select(log => log.Model),
            "upstream_model" => query.Select(log => log.UpstreamModel),
            "channel_id" => query.Select(log => log.ChannelId != null ? log.ChannelId.Value.ToString() : null),
            "owner_username" => from log in query
                                join user in _userRepository.TableNoTracking on log.OwnerUserId equals user.Id
                                select user.Username,
            "path" => query.Select(log => log.Path),
            "request_type" => query.Select(log => log.RequestType),
            _ => Enumerable.Empty<string?>().AsQueryable()
        };

        var queryText = (search?.ToString() ?? string.Empty).Trim();
        if (queryText.Length > 0)
        {
            values = values.Where(value => value != null && value.Contains(queryText));
        }

        return values
            .Where(value => value != null && value != string.Empty)
            .Distinct()
            .OrderBy(value => value)
            .Take(200)
            .AsEnumerable()
            .Select(value => value!)
            .ToList();
    }

    private List<object> DistinctIntValues(
        IQueryable<RequestLog> query,
        string field,
        object? search = null)
    {
        var queryText = (search?.ToString() ?? string.Empty).Trim();
        if (field == "status_code")
        {
            if (queryText.Length > 0 && !TryConvertInt32(queryText, out _))
            {
                return [];
            }
            var statusValues = query.Select(log => log.StatusCode);
            if (queryText.Length > 0 && TryConvertInt32(queryText, out var parsedStatus))
            {
                statusValues = statusValues.Where(value => value == parsedStatus);
            }
            return statusValues
                .Where(value => value.HasValue)
                .Distinct()
                .OrderBy(value => value)
                .Take(200)
                .AsEnumerable()
                .Select(value => (object)value!.Value)
                .ToList();
        }

        // api_key_id: Guid
        var keyValues = query.Select(log => log.ApiKeyId);
        Guid? parsedKey = null;
        if (queryText.Length > 0 && Guid.TryParse(queryText, out var pk))
        {
            parsedKey = pk;
            keyValues = keyValues.Where(value => value == parsedKey);
        }
        return keyValues
            .Where(value => value.HasValue)
            .Distinct()
            .OrderBy(value => value)
            .Take(200)
            .AsEnumerable()
            .Select(value => (object)value!.Value)
            .ToList();
    }

    private List<LogApiKeyFilterOption> DistinctApiKeyOptions(
        IQueryable<RequestLog> query,
        object? search = null)
    {
        var queryText = (search?.ToString() ?? string.Empty).Trim();
        var values = query
            .Select(log => log.ApiKeyId)
            .Where(value => value.HasValue);
        if (queryText.Length > 0)
        {
            var matchingNameIds = _keyRepository.TableNoTracking
                .Where(key => key.Name.Contains(queryText))
                .Select(key => (Guid?)key.Id);
            Guid? parsed = Guid.TryParse(queryText, out var p) ? p : null;
            values = parsed.HasValue
                ? values.Where(value => value == parsed || matchingNameIds.Contains(value))
                : values.Where(value => matchingNameIds.Contains(value));
        }

        var ids = values
            .Distinct()
            .OrderBy(value => value)
            .Take(200)
            .AsEnumerable()
            .Select(value => value!.Value)
            .ToList();
        var names = ReadApiKeyNames(ids.Select(id => (Guid?)id));
        return ids
            .Select(id => new LogApiKeyFilterOption(
                id,
                names.TryGetValue(id, out var name) ? name : null))
            .ToList();
    }

    private static int ParseLogPage(object? page)
    {
        return TryConvertInt32(page, out var parsed)
            ? Math.Max(1, parsed)
            : 1;
    }

    private static int ParseLogPageSize(object? pageSize)
    {
        return TryConvertInt32(pageSize, out var parsed)
            ? Math.Clamp(parsed, 1, 200)
            : 50;
    }

    private static StatsDto EmptyStatsResponse(ResolvedStatsRange resolved)
    {
        return new StatsDto(
            resolved.RangeKey,
            TimestampToIso(resolved.StartTs),
            TimestampToIso(resolved.EndTs),
            resolved.GranularityMinutes,
            PricingDefaults.UsdCnyRate,
            EmptyStatsSummary(),
            [],
            []);
    }

    private static StatsSummaryDto EmptyStatsSummary()
    {
        return new StatsSummaryDto(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);
    }

    private static StatsSummaryDto ReadStatsSummary(
        IReadOnlyList<RequestLog> logs,
        double startTs,
        double endTs,
        int granularityMinutes)
    {
        var requestCount = logs.Count;
        var successCount = logs.Count(IsSuccessfulLog);
        var inputTokens = logs.Sum(log => log.InputTokens);
        var cachedTokens = logs.Sum(log => log.CachedTokens);
        var outputTokens = logs.Sum(log => log.OutputTokens);
        var cost = logs.Sum(log => log.Cost);

        var effectiveEndTs = Math.Min(endTs, UnixTimeSeconds());
        var recentStartTs = Math.Max(startTs, effectiveEndTs - 3600);
        var recentLogs = logs
            .Where(log => log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs)
            .ToList();
        var recentRequestCount = recentLogs.Count;
        var recentInputTokens = recentLogs.Sum(log => log.InputTokens);
        var recentCachedTokens = recentLogs.Sum(log => log.CachedTokens);
        var recentOutputTokens = recentLogs.Sum(log => log.OutputTokens);
        var recentCost = recentLogs.Sum(log => log.Cost);

        var latestWindowStartTs = Math.Max(startTs, effectiveEndTs - granularityMinutes * 60.0);
        var latestWindowLogs = logs
            .Where(log => log.CreatedAt >= latestWindowStartTs && log.CreatedAt < effectiveEndTs)
            .ToList();
        var latestTokens = latestWindowLogs.Sum(log => log.InputTokens + log.CachedTokens + log.OutputTokens);

        return new StatsSummaryDto(
            requestCount,
            successCount,
            recentRequestCount,
            inputTokens,
            cachedTokens,
            outputTokens,
            inputTokens + cachedTokens + outputTokens,
            recentInputTokens + recentCachedTokens + recentOutputTokens,
            Math.Round(cost, 6),
            Math.Round(recentCost, 6),
            latestWindowLogs.Count > 0 ? Math.Round((double)latestWindowLogs.Count / granularityMinutes, 2) : 0,
            latestTokens > 0 ? Math.Round((double)latestTokens / granularityMinutes, 2) : 0);
    }

    private static List<ModelDistributionDto> ReadModelDistribution(IReadOnlyList<RequestLog> logs)
    {
        return logs
            .GroupBy(log => string.IsNullOrEmpty(log.Model) ? "unknown" : log.Model)
            .Select(group => new ModelDistributionDto(group.Key!, group.Count()))
            .OrderByDescending(item => item.Count)
            .Take(20)
            .ToList();
    }

    private static bool IsSuccessfulLog(RequestLog log)
    {
        return log.LifecycleStatus == ProxyRequestLifecycleStatus.Success
            || (log.LifecycleStatus is null && log.StatusCode < 400 && string.IsNullOrEmpty(log.Error));
    }

    private static RequestLogEventDto MapRequestLogEvent(RequestLog log, string ownerUsername)
    {
        return new RequestLogEventDto(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.ProcessingStartedAt,
            log.CompletedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId?.ToString(),
            log.RequestType,
            log.ParentRequestLogId,
            log.IsStream,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            ownerUsername,
            log.ApiKeyId,
            log.Error,
            NormalizeRequestStatus(log.LifecycleStatus, log.StatusCode, log.Error));
    }

    private static RequestLogDto MapRequestLog(
        RequestLog log,
        RequestLogDetail? detail,
        IReadOnlyList<RequestLogStreamLine> streamLines,
        string ownerUsername)
    {
        return new RequestLogDto(
            log.Id,
            log.RequestId,
            log.CreatedAt,
            log.ProcessingStartedAt,
            log.CompletedAt,
            log.Method,
            log.Path,
            log.ClientIp,
            log.Model,
            log.UpstreamModel,
            log.ChannelId,
            log.RequestType,
            log.ParentRequestLogId,
            log.IsStream,
            log.TtftMs,
            log.DurationMs,
            log.StatusCode,
            log.InputTokens,
            log.CachedTokens,
            log.OutputTokens,
            log.Cost,
            ownerUsername,
            log.ApiKeyId,
            log.Error,
            detail?.RequestHeaders,
            detail?.RequestBody,
            detail?.UpstreamRequestBody,
            detail?.UpstreamResponseBody,
            detail?.ResponseBody,
            detail?.WebSearchJson,
            detail?.OcrJson,
            detail?.StreamTimingsJson,
            streamLines
                .OrderBy(item => item.Sequence)
                .Select(item => new RequestLogStreamLineDto(
                    item.Sequence,
                    item.OccurredAt,
                    item.Source,
                    item.RawLine))
                .ToList(),
            NormalizeRequestStatus(log.LifecycleStatus, log.StatusCode, log.Error));
    }

    private static string NormalizeRequestStatus(string? lifecycleStatus, int? statusCode, string? error)
    {
        if (!string.IsNullOrWhiteSpace(lifecycleStatus))
        {
            return lifecycleStatus;
        }

        var status = statusCode ?? 0;
        return status >= 400 || !string.IsNullOrWhiteSpace(error)
            ? ProxyRequestLifecycleStatus.Failed
            : ProxyRequestLifecycleStatus.Success;
    }

    private static ResolvedStatsRange ResolveStatsRange(
        string? rangeKey,
        object? startTs,
        object? endTs)
    {
        var normalizedRange = (rangeKey ?? "1h").Trim();
        var now = UnixTimeSeconds();
        if (normalizedRange == "custom")
        {
            var parsedEnd = ParseTimestamp(endTs);
            var parsedStart = ParseTimestamp(startTs);
            var endValue = parsedEnd ?? now;
            var startValue = parsedStart ?? endValue - 3600;
            if (startValue >= endValue)
            {
                startValue = endValue - 3600;
            }

            return new ResolvedStatsRange(
                "custom",
                startValue,
                endValue,
                StatsGranularityForSeconds(endValue - startValue));
        }

        if (!StatsRangeHours.ContainsKey(normalizedRange))
        {
            normalizedRange = "1h";
        }

        var seconds = StatsRangeHours[normalizedRange] * 3600.0;
        return new ResolvedStatsRange(
            normalizedRange,
            now - seconds,
            now,
            StatsRangeGranularity[normalizedRange]);
    }

    private static int StatsGranularityForSeconds(double seconds)
    {
        var minutes = Math.Max(1, seconds / 60);
        const int targetPoints = 72;
        var rawGranularity = Math.Max(1, (int)Math.Floor((minutes + targetPoints - 1) / targetPoints));
        foreach (var choice in new[] { 1, 3, 5, 10, 15, 30, 60, 120, 360, 720, 1440 })
        {
            if (rawGranularity <= choice)
            {
                return choice;
            }
        }

        return 1440;
    }

    private static double? ParseTimestamp(object? value)
    {
        if (IsEmptyLogFilterValue(value) || !TryConvertDouble(value, out var parsed))
        {
            return null;
        }

        if (parsed > 10_000_000_000)
        {
            parsed /= 1000;
        }

        return parsed > 0 ? parsed : null;
    }

    private static string TimestampToIso(double timestamp)
    {
        var milliseconds = (long)Math.Floor(timestamp * 1000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static bool IsEmptyLogFilterValue(object? value)
    {
        if (value is null)
        {
            return true;
        }

        return value is string text && text.Trim().Length == 0;
    }

    private static bool TryConvertInt64(object? value, out long parsed)
    {
        try
        {
            parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool TryConvertInt32(object? value, out int parsed)
    {
        try
        {
            parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
    }

    private static bool TryConvertDouble(object? value, out double parsed)
    {
        try
        {
            parsed = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            parsed = 0;
            return false;
        }
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

    private sealed class ResolvedStatsRange
    {
        public ResolvedStatsRange(string rangeKey, double startTs, double endTs, int granularityMinutes)
        {
            RangeKey = rangeKey;
            StartTs = startTs;
            EndTs = endTs;
            GranularityMinutes = granularityMinutes;
        }

        public string RangeKey { get; }

        public double StartTs { get; }

        public double EndTs { get; }

        public int GranularityMinutes { get; }
    }
}
