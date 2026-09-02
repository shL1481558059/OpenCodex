using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Caching;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

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

    private static readonly IReadOnlyList<string> RequestTypeValues =
    [
        ProxyRequestTypes.Main,
        ProxyRequestTypes.Ocr,
        ProxyRequestTypes.Attempt,
        ProxyRequestTypes.Diagnostic
    ];

    private static readonly IReadOnlyDictionary<string, (string OptionKey, string OptionType)> LogFilterFields =
        new Dictionary<string, (string OptionKey, string OptionType)>(StringComparer.Ordinal)
        {
            ["request_id"] = ("request_ids", "text"),
            ["conversation_key"] = ("conversation_keys", "text"),
            ["conversation_turn_id"] = ("conversation_turn_ids", "text"),
            ["conversation_window_id"] = ("conversation_window_ids", "text"),
            ["previous_response_id"] = ("previous_response_ids", "text"),
            ["model"] = ("models", "text"),
            ["upstream_model"] = ("upstream_models", "text"),
            ["channel_id"] = ("channel_ids", "select_option"),
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

    private readonly IWorkContext _workContext;
    private readonly IOpenCodexDbContext _dbContext;
    private readonly IRepository<RequestLog> _logRepository;
    private readonly IRepository<AccessApiKey> _keyRepository;
    private readonly IRepository<User> _userRepository;
    private readonly IRepository<Channel> _channelRepository;
    private readonly IRepository<RequestLogContentRef> _contentRefRepository;
    private readonly IRepository<LogContentManifestChunk> _manifestChunkRepository;
    private readonly IRepository<LogContentManifest> _manifestRepository;
    private readonly IRepository<LogContentBlock> _contentBlockRepository;
    private readonly IChannelCapacityService _channelCapacity;
    private readonly IProxySettingsService _proxySettings;
    private readonly LogContentStore _contentStore;
    private readonly IMemoryCache _memoryCache;
    private static readonly TimeSpan ChannelConfigCacheTtl = TimeSpan.FromSeconds(10);

    public ObservabilityService(
        IWorkContext workContext,
        IOpenCodexDbContext dbContext,
        IRepository<RequestLog> logRepository,
        IRepository<AccessApiKey> keyRepository,
        IRepository<User> userRepository,
        IRepository<Channel> channelRepository,
        IRepository<RequestLogContentRef> contentRefRepository,
        IRepository<LogContentManifestChunk> manifestChunkRepository,
        IRepository<LogContentManifest> manifestRepository,
        IRepository<LogContentBlock> contentBlockRepository,
        IChannelCapacityService channelCapacity,
        IProxySettingsService proxySettings,
        IMemoryCache memoryCache)
    {
        _workContext = workContext;
        _dbContext = dbContext;
        _logRepository = logRepository;
        _keyRepository = keyRepository;
        _userRepository = userRepository;
        _channelRepository = channelRepository;
        _contentRefRepository = contentRefRepository;
        _manifestChunkRepository = manifestChunkRepository;
        _manifestRepository = manifestRepository;
        _contentBlockRepository = contentBlockRepository;
        _channelCapacity = channelCapacity;
        _proxySettings = proxySettings;
        _memoryCache = memoryCache;
        _contentStore = new LogContentStore(dbContext);
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

    public ApiOpResult<ActiveChannelQueueResponse> ReadActiveChannelQueue()
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var queue = QueryActiveChannelQueue(currentUsername, isSuperadmin);
        return ApiOpResult<ActiveChannelQueueResponse>.Succeed(ActiveChannelQueueResponse.From(queue));
    }

    public ApiOpResult<IReadOnlyList<RecentErrorItemResponse>> ReadRecentErrors(int limit)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var items = QueryRecentErrors(limit, currentUsername, isSuperadmin);
        return ApiOpResult<IReadOnlyList<RecentErrorItemResponse>>.Succeed(
            items.Select(RecentErrorItemResponse.From).ToList());
    }

    private List<RecentErrorItemDto> QueryRecentErrors(
        int limit,
        string currentUsername,
        bool isSuperadmin)
    {
        var query = _logRepository.TableNoTracking
            .Where(ExcludedRequestTypePredicate());

        if (!isSuperadmin)
        {
            var userId = _userRepository.TableNoTracking
                .Where(u => u.Username == currentUsername)
                .Select(u => u.Id)
                .FirstOrDefault();
            if (userId == Guid.Empty) return [];
            query = query.Where(log => log.OwnerUserId == userId);
        }

        var errorLogs = query
            .Where(log =>
                log.LifecycleStatus == ProxyRequestLifecycleStatus.Failed
                || (log.LifecycleStatus == null && (log.StatusCode >= 400 || !string.IsNullOrEmpty(log.Error))))
            .OrderByDescending(log => log.CreatedAt)
            .Take(limit)
            .Select(log => new RecentErrorRow
            {
                Id = log.Id,
                CreatedAt = log.CreatedAt,
                Model = log.Model,
                UpstreamModel = log.UpstreamModel,
                ChannelId = log.ChannelId,
                StatusCode = log.StatusCode,
                Error = log.Error
            })
            .ToList();

        if (errorLogs.Count == 0)
        {
            return [];
        }

        var channelIdTexts = errorLogs
            .Select(log => log.ChannelId?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var channelNames = ReadChannelNames(channelIdTexts);

        return errorLogs.Select(log =>
        {
            var channelName = log.ChannelId.HasValue
                && channelNames.TryGetValue(log.ChannelId.Value, out var name)
                ? name
                : null;
            return new RecentErrorItemDto(
                log.Id,
                log.CreatedAt,
                log.Model,
                log.UpstreamModel,
                channelName,
                log.StatusCode,
                log.Error);
        }).ToList();
    }


    public ApiOpResult<ClearLogsResponse> ClearLogs()
    {
        var currentUser = _workContext.RequireUser();
        if (currentUser.Role != "superadmin")
        {
            return ApiOpResult<ClearLogsResponse>.Fail(403, "only superadmin can clear logs");
        }

        // 内容块由多个请求共享，清空时必须按外键依赖顺序删除：先删引用与清单块，
        // 再删日志、清单，最后删物理块（LogContentManifestChunks.BlockId 与
        // RequestLogContentRefs.ManifestId 均为 Restrict）。ExecuteDelete 的返回值即受影响行数。
        // 全部删除放进同一个显式事务：ExecuteDelete 立即执行、不参与 SaveChanges 的隐式事务，
        // 中途失败时回滚整批，避免留下孤儿 manifest / block。
        int deletedContentRefs;
        int deletedLogs;
        int deletedContentBlocks;
        using (var transaction = _dbContext.Database.BeginTransaction())
        {
            deletedContentRefs = _contentRefRepository.ExecuteDeleteAll();
            _manifestChunkRepository.ExecuteDeleteAll();
            deletedLogs = _logRepository.ExecuteDeleteAll();
            _manifestRepository.ExecuteDeleteAll();
            deletedContentBlocks = _contentBlockRepository.ExecuteDeleteAll();
            transaction.Commit();
        }

        return ApiOpResult<ClearLogsResponse>.Succeed(new ClearLogsResponse(
            deletedLogs,
            deletedContentRefs,
            deletedContentBlocks));
    }

    public ApiOpResult<StatsSummaryResponse> ReadStatsSummary(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        var scopedFilters = ScopedFilters(filters, currentUsername, isSuperadmin);
        var baseQuery = ApplyLogFilters(
            _logRepository.TableNoTracking
                .Where(log => log.CreatedAt >= resolved.StartTs && log.CreatedAt < resolved.EndTs),
            scopedFilters);

        var summary = QueryStatsSummary(baseQuery, resolved);

        return ApiOpResult<StatsSummaryResponse>.Succeed(StatsSummaryResponse.From(summary));
    }

    public ApiOpResult<IReadOnlyList<StatsPointResponse>> ReadStatsTimeseries(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        var scopedFilters = ScopedFilters(filters, currentUsername, isSuperadmin);
        var query = ApplyLogFilters(
            _logRepository.TableNoTracking
                .Where(log => log.CreatedAt >= resolved.StartTs && log.CreatedAt < resolved.EndTs),
            scopedFilters);
        var points = QueryStatsPoints(query, resolved);

        return ApiOpResult<IReadOnlyList<StatsPointResponse>>.Succeed(
            points.Select(StatsPointResponse.From).ToList());
    }

    public ApiOpResult<IReadOnlyList<StatsModelDistributionResponse>> ReadStatsModelDistribution(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        var scopedFilters = ScopedFilters(filters, currentUsername, isSuperadmin);
        var query = ApplyLogFilters(
            _logRepository.TableNoTracking
                .Where(log => log.CreatedAt >= resolved.StartTs && log.CreatedAt < resolved.EndTs),
            scopedFilters);
        var distribution = QueryModelDistribution(query);
        return ApiOpResult<IReadOnlyList<StatsModelDistributionResponse>>.Succeed(
            distribution.Select(StatsModelDistributionResponse.From).ToList());
    }

    public ApiOpResult<IReadOnlyList<ErrorDistributionResponse>> ReadStatsErrorDistribution(
        string rangeKey,
        object? startTs,
        object? endTs,
        IReadOnlyDictionary<string, object?> filters)
    {
        var (currentUsername, isSuperadmin) = CurrentScope();
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        var scopedFilters = ScopedFilters(filters, currentUsername, isSuperadmin);
        var query = ApplyLogFilters(
            _logRepository.TableNoTracking
                .Where(log => log.CreatedAt >= resolved.StartTs && log.CreatedAt < resolved.EndTs),
            scopedFilters);
        var distribution = QueryErrorDistribution(query);
        return ApiOpResult<IReadOnlyList<ErrorDistributionResponse>>.Succeed(
            distribution.Select(ErrorDistributionResponse.From).ToList());
    }

    private Dictionary<Guid, string> BuildOwnerMap(IReadOnlyList<RequestLogRow> logs)
    {
        var ownerIds = logs.Select(log => log.OwnerUserId).Distinct().ToList();
        return ownerIds.Count > 0
            ? _userRepository.TableNoTracking
                .Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
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
        return _userRepository.TableNoTracking
            .Where(u => u.Username == normalized)
            .Select(u => u.Id)
            .FirstOrDefault();
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
            .OrderByDescending(log => log.CreatedAt)
            .Skip(offset)
            .Take(parsedPageSize)
            .Select(log => new RequestLogRow
            {
                Id = log.Id,
                RequestId = log.RequestId,
                CreatedAt = log.CreatedAt,
                ProcessingStartedAt = log.ProcessingStartedAt,
                CompletedAt = log.CompletedAt,
                Method = log.Method,
                Path = log.Path,
                ClientIp = log.ClientIp,
                Model = log.Model,
                UpstreamModel = log.UpstreamModel,
                ChannelId = log.ChannelId,
                RequestType = log.RequestType,
                ParentRequestLogId = log.ParentRequestLogId,
                ConversationKey = log.ConversationKey,
                ConversationTurnId = log.ConversationTurnId,
                ConversationWindowId = log.ConversationWindowId,
                PreviousResponseId = log.PreviousResponseId,
                IsStream = log.IsStream,
                TtftMs = log.TtftMs,
                DurationMs = log.DurationMs,
                StatusCode = log.StatusCode,
                InputTokens = log.InputTokens,
                CachedTokens = log.CachedTokens,
                OutputTokens = log.OutputTokens,
                Cost = log.Cost,
                CostCurrency = log.CostCurrency,
                OwnerUserId = log.OwnerUserId,
                ApiKeyId = log.ApiKeyId,
                Error = log.Error,
                LifecycleStatus = log.LifecycleStatus
            })
            .ToList();
        var ownerMap = BuildOwnerMap(logs);
        var attemptStats = BuildAttemptStats(logs);
        var events = logs
            .Select(log => MapRequestLogEvent(
                log,
                ownerMap.TryGetValue(log.OwnerUserId, out var name) ? name : string.Empty,
                attemptStats.TryGetValue(log.Id, out var stats) ? stats : (AttemptCount: 0, FailedAttemptCount: 0)))
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

        var query = ApplyLogFilters(
            _logRepository.TableNoTracking,
            filters ?? new Dictionary<string, object?>(),
            excludeAttemptsByDefault: false);
        var log = query
            .Where(item => item.Id == guidId)
            .Select(item => new RequestLogRow
            {
                Id = item.Id,
                RequestId = item.RequestId,
                CreatedAt = item.CreatedAt,
                ProcessingStartedAt = item.ProcessingStartedAt,
                CompletedAt = item.CompletedAt,
                Method = item.Method,
                Path = item.Path,
                ClientIp = item.ClientIp,
                Model = item.Model,
                UpstreamModel = item.UpstreamModel,
                ChannelId = item.ChannelId,
                RequestType = item.RequestType,
                ParentRequestLogId = item.ParentRequestLogId,
                ConversationKey = item.ConversationKey,
                ConversationTurnId = item.ConversationTurnId,
                ConversationWindowId = item.ConversationWindowId,
                PreviousResponseId = item.PreviousResponseId,
                IsStream = item.IsStream,
                TtftMs = item.TtftMs,
                DurationMs = item.DurationMs,
                StatusCode = item.StatusCode,
                InputTokens = item.InputTokens,
                CachedTokens = item.CachedTokens,
                OutputTokens = item.OutputTokens,
                Cost = item.Cost,
                CostCurrency = item.CostCurrency,
                OwnerUserId = item.OwnerUserId,
                ApiKeyId = item.ApiKeyId,
                Error = item.Error,
                LifecycleStatus = item.LifecycleStatus
            })
            .FirstOrDefault();
        if (log is null)
        {
            return null;
        }

        var content = _contentStore.Read(log.Id);
        var ownerUsername = _userRepository.TableNoTracking
            .Where(u => u.Id == log.OwnerUserId)
            .Select(u => u.Username)
            .FirstOrDefault() ?? string.Empty;
        var attemptStats = BuildAttemptStats([log]);
        return MapRequestLog(
            log,
            content,
            ownerUsername,
            attemptStats.TryGetValue(log.Id, out var stats)
                ? stats
                : (AttemptCount: 0, FailedAttemptCount: 0));
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
                ["request_types"] = RequestTypeValues.ToList()
            };
        }

        if (!LogFilterFields.TryGetValue(field, out var option))
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        var logs = ApplyLogFilters(_logRepository.TableNoTracking, filters ?? new Dictionary<string, object?>());
        var values = field == "api_key_id"
            ? (object)DistinctApiKeyOptions(logs, query)
            : field == "channel_id"
            ? (object)DistinctChannelOptions(logs, query)
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

        var points = QueryStatsPoints(query, resolved);
        var modelDistribution = QueryModelDistribution(query);
        var errorDistribution = QueryErrorDistribution(query);
        var summary = QueryStatsSummary(query, resolved);
        var usdCnyRate = ResolveUsdCnyRate();

        return new StatsDto(
            resolved.RangeKey,
            TimestampToIso(resolved.StartTs),
            TimestampToIso(resolved.EndTs),
            resolved.GranularityMinutes,
            usdCnyRate,
            summary,
            points,
            modelDistribution,
            errorDistribution);
    }

    private IQueryable<RequestLog> ApplyLogFilters(
        IQueryable<RequestLog> query,
        IReadOnlyDictionary<string, object?> filters,
        bool excludeAttemptsByDefault = true)
    {
        var hasRequestTypeFilter = false;
        foreach (var (field, value) in filters)
        {
            if (IsEmptyLogFilterValue(value))
            {
                continue;
            }

            if (field == "request_type")
            {
                hasRequestTypeFilter = true;
            }

            query = ApplyLogFilter(query, field, value);
        }

        if (excludeAttemptsByDefault && !hasRequestTypeFilter)
        {
            query = query.Where(ExcludedRequestTypePredicate());
        }

        return query;
    }

    private static System.Linq.Expressions.Expression<Func<RequestLog, bool>> ExcludedRequestTypePredicate()
    {
        // 保持显式的 != 链，确保 EF Core 能将条件翻译成 SQL（SQLite / PostgreSQL 均可）。
        return log => log.RequestType == null
            || (log.RequestType != ProxyRequestTypes.Attempt
                && log.RequestType != ProxyRequestTypes.Diagnostic);
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
            "conversation_key" when text.Length > 0 => query.Where(log => log.ConversationKey != null && log.ConversationKey.Contains(text)),
            "conversation_turn_id" when text.Length > 0 => query.Where(log => log.ConversationTurnId != null && log.ConversationTurnId.Contains(text)),
            "conversation_window_id" when text.Length > 0 => query.Where(log => log.ConversationWindowId != null && log.ConversationWindowId.Contains(text)),
            "previous_response_id" when text.Length > 0 => query.Where(log => log.PreviousResponseId != null && log.PreviousResponseId.Contains(text)),
            "model" when text.Length > 0 => query.Where(log => log.Model != null && log.Model.Contains(text)),
            "upstream_model" when text.Length > 0 => query.Where(log => log.UpstreamModel != null && log.UpstreamModel.Contains(text)),
            "channel_id" when text.Length > 0 && Guid.TryParse(text, out var channelId) => query.Where(log => log.ChannelId == channelId),
            "owner_username" when text.Length > 0 => ApplyOwnerUsernameFilter(query, text),
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
            "parent_request_log_id" when text.Length > 0 && Guid.TryParse(text, out var parentId) => query.Where(log => log.ParentRequestLogId == parentId),
            _ => query
        };
    }

    private IQueryable<RequestLog> ApplyOwnerUsernameFilter(
        IQueryable<RequestLog> query,
        string username)
    {
        // 在进入 lambda 之前解析一次用户，避免 EF 参数提取时在表达式内反复触发查询。
        var ownerUserId = ResolveOwnerUserIdFilter(username);
        return ownerUserId == Guid.Empty
            ? query.Where(log => false)
            : query.Where(log => log.OwnerUserId == ownerUserId);
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
            "conversation_key" => query.Select(log => log.ConversationKey),
            "conversation_turn_id" => query.Select(log => log.ConversationTurnId),
            "conversation_window_id" => query.Select(log => log.ConversationWindowId),
            "previous_response_id" => query.Select(log => log.PreviousResponseId),
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


    private List<SelectOption<Guid>> DistinctChannelOptions(
        IQueryable<RequestLog> query,
        object? search = null)
    {
        var queryText = (search?.ToString() ?? string.Empty).Trim();
        var values = query
            .Select(log => log.ChannelId)
            .Where(value => value.HasValue);
        if (queryText.Length > 0)
        {
            var matchingNameIds = _channelRepository.TableNoTracking
                .Where(channel => channel.Name.Contains(queryText))
                .Select(channel => (Guid?)channel.Id);
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
        var names = ReadChannelNames(ids.Select(id => (Guid?)id));
        return ids
            .Select(id => new SelectOption<Guid>(
                id,
                names.TryGetValue(id, out var name) ? name : null))
            .ToList();
    }

    private Dictionary<Guid, string> ReadChannelNames(
        IEnumerable<Guid?> channelIds)
    {
        var ids = channelIds
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
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
            .ToDictionary(item => item.Id, item => item.Name);
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

    private ActiveChannelQueueDto QueryActiveChannelQueue(string currentUsername, bool isSuperadmin)
    {
        var channels = ReadScopedChannels(currentUsername, isSuperadmin)
            .Select(channel =>
            {
                var channelId = channel.Id.ToString();
                var models = _channelCapacity
                    .GetActiveModelUsages(channel.OwnerUsername, channelId)
                    .Select(model => new ActiveChannelQueueModelDto(
                        model.Model,
                        model.UpstreamModel,
                        model.ActiveRequests))
                    .ToList();

                return new ActiveChannelQueueItemDto(
                    channelId,
                    string.IsNullOrWhiteSpace(channel.Name) ? "未命名渠道" : channel.Name,
                    _channelCapacity.GetActiveRequests(channel.OwnerUsername, channelId),
                    models);
            })
            .Where(item => item.ProcessingCount > 0)
            .OrderByDescending(item => item.ProcessingCount)
            .ThenBy(item => item.ChannelName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channels.Count == 0)
        {
            return new ActiveChannelQueueDto(
                TimestampToIso(UnixTimeSeconds()),
                []);
        }

        return new ActiveChannelQueueDto(
            TimestampToIso(UnixTimeSeconds()),
            channels);
    }

    private IReadOnlyList<ChannelDto> ReadScopedChannels(string currentUsername, bool isSuperadmin)
    {
        // ObservabilityService 只需要渠道的 id/name/owner/容量等轻量字段,
        // 不应污染 ChannelService 的 Channels 缓存(后者含完整 models/headers/compat)。
        var allChannels = _memoryCache.GetOrCreate(CacheKeys.ChannelObservation, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ChannelConfigCacheTtl;
            return LoadAllScopedChannelDtos();
        });

        if (allChannels is null)
        {
            return [];
        }

        return isSuperadmin
            ? allChannels
            : allChannels
                .Where(dto => string.Equals(dto.OwnerUsername, currentUsername, StringComparison.Ordinal))
                .ToList();
    }

    private IReadOnlyList<ChannelDto> LoadAllScopedChannelDtos()
    {
        var channels = _channelRepository.TableNoTracking.ToList();
        if (channels.Count == 0)
        {
            return [];
        }

        var ownerIds = channels.Select(channel => channel.OwnerUserId).Distinct().ToList();
        var ownerMap = _userRepository.TableNoTracking
            .Where(user => ownerIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Username })
            .ToDictionary(user => user.Id, user => user.Username);

        return channels
            .Select(channel => new ChannelDto(
                channel.Id,
                channel.OwnerUserId,
                ownerMap.TryGetValue(channel.OwnerUserId, out var ownerUsername) ? ownerUsername : string.Empty,
                channel.Position,
                channel.Name,
                channel.GroupName,
                channel.Type,
                channel.BaseUrl,
                channel.ApiKey,
                channel.AuthMode,
                new Dictionary<string, object?>(),
                channel.TimeoutSeconds,
                channel.CircuitBreakDurationSeconds,
                channel.RetryCount,
                channel.Priority,
                channel.Capacity,
                new Dictionary<string, object?>(),
                [],
                channel.Enabled))
            .ToList();
    }
    private StatsSummaryDto QueryStatsSummary(
        IQueryable<RequestLog> query,
        ResolvedStatsRange resolved)
    {
        var effectiveEndTs = Math.Min(resolved.EndTs, UnixTimeSeconds());
        var recentStartTs = Math.Max(resolved.StartTs, effectiveEndTs - 3600);
        var latestWindowStartTs = Math.Max(
            resolved.StartTs,
            effectiveEndTs - resolved.GranularityMinutes * 60.0);

        // 一次条件聚合取回全部标量，recent / latest 窗口用 CASE WHEN 表达，
        // 避免每个面板各发一条 SQL。GroupBy(_ => 1) 在空表时返回 0 行，
        // FirstOrDefault() 得到 null，回退成全 0 的摘要。
        var row = query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                RequestCount = g.Count(),
                InputTokens = g.Sum(log => log.InputTokens),
                CachedTokens = g.Sum(log => log.CachedTokens),
                OutputTokens = g.Sum(log => log.OutputTokens),
                Cost = g.Sum(log => log.Cost),
                RecentRequestCount = g.Count(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs),
                RecentInputTokens = g.Sum(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs
                        ? log.InputTokens
                        : 0),
                RecentCachedTokens = g.Sum(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs
                        ? log.CachedTokens
                        : 0),
                RecentOutputTokens = g.Sum(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs
                        ? log.OutputTokens
                        : 0),
                RecentCost = g.Sum(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs
                        ? log.Cost
                        : 0),
                LatestWindowCount = g.Count(log =>
                    log.CreatedAt >= latestWindowStartTs && log.CreatedAt < effectiveEndTs),
                LatestTokens = g.Sum(log =>
                    log.CreatedAt >= latestWindowStartTs && log.CreatedAt < effectiveEndTs
                        ? log.InputTokens + log.OutputTokens
                        : 0)
            })
            .FirstOrDefault();
        if (row is null)
        {
            return new StatsSummaryDto(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0);
        }

        // IsSuccessfulPredicate() 是 Expression<Func<...>>，IGrouping 的 Count 只收
        // Func<...>，表达式树里没法直接复用同一个 predicate，因此 successCount
        // 保留为独立的第 2 条查询，口径不变。
        var successCount = query.Count(IsSuccessfulPredicate());

        // 成本按来源币种分组聚合（低基数，扫描行数与主聚合一致），
        // 再用当前汇率折算成人民币/美元两种口径。单条日志的原始币种成本
        // 仍然保留在 Cost / CostCurrency 中，不参与折算。
        var usdCnyRate = ResolveUsdCnyRate();
        var currencyCosts = query
            .GroupBy(log => log.CostCurrency)
            .Select(g => new
            {
                Currency = g.Key ?? "USD",
                TotalCost = g.Sum(log => log.Cost),
                RecentCost = g.Sum(log =>
                    log.CreatedAt >= recentStartTs && log.CreatedAt < effectiveEndTs
                        ? log.Cost
                        : 0)
            })
            .ToList();

        double cnyCost = 0;
        double usdCost = 0;
        double recentCnyCost = 0;
        double recentUsdCost = 0;
        foreach (var item in currencyCosts)
        {
            if (string.Equals(item.Currency, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                cnyCost += item.TotalCost;
                recentCnyCost += item.RecentCost;
            }
            else
            {
                // 非 CNY（默认 USD）先折算成人民币，再统一除汇率得到美元口径。
                cnyCost += item.TotalCost * usdCnyRate;
                recentCnyCost += item.RecentCost * usdCnyRate;
            }
        }

        usdCost = usdCnyRate > 0 ? cnyCost / usdCnyRate : cnyCost;
        recentUsdCost = usdCnyRate > 0 ? recentCnyCost / usdCnyRate : recentCnyCost;

        return new StatsSummaryDto(
            row.RequestCount,
            successCount,
            row.RecentRequestCount,
            row.InputTokens,
            row.CachedTokens,
            row.OutputTokens,
            row.InputTokens + row.OutputTokens,
            row.RecentInputTokens + row.RecentOutputTokens,
            Math.Round(row.Cost, 6),
            Math.Round(row.RecentCost, 6),
            row.LatestWindowCount > 0
                ? Math.Round((double)row.LatestWindowCount / resolved.GranularityMinutes, 2)
                : 0,
            row.LatestTokens > 0
                ? Math.Round((double)row.LatestTokens / resolved.GranularityMinutes, 2)
                : 0,
            Math.Round(cnyCost, 6),
            Math.Round(usdCost, 6),
            Math.Round(recentCnyCost, 6),
            Math.Round(recentUsdCost, 6));
    }

    private IReadOnlyList<StatsPointDto> QueryStatsPoints(
        IQueryable<RequestLog> query,
        ResolvedStatsRange resolved)
    {
        var bucketSeconds = resolved.GranularityMinutes * 60.0;
        var bucketCount = Math.Max(
            1,
            (int)Math.Floor((resolved.EndTs - resolved.StartTs + bucketSeconds - 1) / bucketSeconds));

        // 桶号在数据库端计算，SUM/COUNT 全部下推，只把聚合结果拉回内存。
        // Math.Floor 保持 double 语义，避免 (long) 被翻译成 float->bigint 的
        // PostgreSQL 四舍五入（与 SQLite 的截断不一致），到内存侧再转 int。
        var grouped = query
            .GroupBy(log => Math.Floor((log.CreatedAt!.Value - resolved.StartTs) / bucketSeconds))
            .Select(group => new
            {
                Bucket = group.Key,
                Count = group.Count(),
                Cost = group.Sum(log => log.Cost),
                InputTokens = group.Sum(log => log.InputTokens),
                CachedTokens = group.Sum(log => log.CachedTokens),
                OutputTokens = group.Sum(log => log.OutputTokens),
                // 用 double 累加避免大桶内 TTFT 总和溢出 int，且与原内存 Average 的浮点语义一致。
                TtftSum = group.Sum(log => log.TtftMs > 0 ? (double?)log.TtftMs : null),
                TtftCount = group.Count(log => log.TtftMs > 0)
            })
            .ToList();
        var byBucket = grouped.ToDictionary(item => (int)item.Bucket);

        // 每个时间桶按来源币种聚合成本，低基数（USD/CNY），只多一次下推的
        // GROUP BY (bucket, currency)，再折算成人民币/美元口径供前端双币种展示。
        var usdCnyRate = ResolveUsdCnyRate();
        var costByCurrency = query
            .GroupBy(log => new
            {
                Bucket = Math.Floor((log.CreatedAt!.Value - resolved.StartTs) / bucketSeconds),
                Currency = log.CostCurrency
            })
            .Select(g => new
            {
                Bucket = g.Key.Bucket,
                Currency = g.Key.Currency ?? "USD",
                Cost = g.Sum(log => log.Cost)
            })
            .ToList();
        var cnyByBucket = new Dictionary<int, double>();
        var usdByBucket = new Dictionary<int, double>();
        foreach (var item in costByCurrency)
        {
            var bucket = (int)item.Bucket;
            var cost = item.Cost;
            if (string.Equals(item.Currency, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                cnyByBucket.TryGetValue(bucket, out var existing);
                cnyByBucket[bucket] = existing + cost;
                usdByBucket.TryGetValue(bucket, out var existingUsd);
                usdByBucket[bucket] = existingUsd + cost / usdCnyRate;
            }
            else
            {
                cnyByBucket.TryGetValue(bucket, out var existing);
                cnyByBucket[bucket] = existing + cost * usdCnyRate;
                usdByBucket.TryGetValue(bucket, out var existingUsd);
                usdByBucket[bucket] = existingUsd + cost;
            }
        }

        var points = new List<StatsPointDto>(bucketCount);
        for (var index = 0; index < bucketCount; index++)
        {
            var bucketEnd = resolved.StartTs + (index + 1) * bucketSeconds;
            if (!byBucket.TryGetValue(index, out var item))
            {
                // GroupBy 不会产生空桶，这里补零，保证曲线点数完整。
                points.Add(new StatsPointDto(
                    TimestampToIso(bucketEnd),
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    0,
                    cnyByBucket.TryGetValue(index, out var emptyCny) ? emptyCny : 0,
                    usdByBucket.TryGetValue(index, out var emptyUsd) ? emptyUsd : 0));
                continue;
            }

            // TTFT 平均只统计 TtftMs > 0 的请求，与原来的内存 Average 语义一致。
            var avgTtft = item.TtftCount > 0 && item.TtftSum.HasValue
                ? item.TtftSum.Value / (double)item.TtftCount
                : (double?)null;
            var cacheDenominator = item.InputTokens + item.CachedTokens;
            points.Add(new StatsPointDto(
                TimestampToIso(bucketEnd),
                Math.Round(item.Cost, 6),
                item.InputTokens,
                item.CachedTokens,
                item.OutputTokens,
                avgTtft is null ? null : Math.Round(avgTtft.Value, 1),
                cacheDenominator > 0
                    ? Math.Round((double)item.CachedTokens / cacheDenominator, 4)
                    : null,
                item.Count > 0
                    ? Math.Round((double)item.Count / resolved.GranularityMinutes, 2)
                    : 0,
                cnyByBucket.TryGetValue(index, out var cny) ? Math.Round(cny, 6) : 0,
                usdByBucket.TryGetValue(index, out var usd) ? Math.Round(usd, 6) : 0));
        }

        return points;
    }

    private double ResolveUsdCnyRate()
    {
        var rate = _proxySettings.GetDecimal(
            PricingDefaults.UsdCnyRateSettingKey,
            (decimal)PricingDefaults.UsdCnyRate);
        return rate > 0 ? (double)rate : PricingDefaults.UsdCnyRate;
    }

    private static List<ModelDistributionDto> QueryModelDistribution(
        IQueryable<RequestLog> query)
    {
        return query
            .GroupBy(log => log.Model == null || log.Model == "" ? "unknown" : log.Model)
            .Select(group => new
            {
                Model = group.Key ?? "unknown",
                Count = group.Count()
            })
            .OrderByDescending(item => item.Count)
            .Take(20)
            .ToList()
            .Select(item => new ModelDistributionDto(item.Model, item.Count))
            .ToList();
    }

    private List<ErrorDistributionDto> QueryErrorDistribution(
        IQueryable<RequestLog> query)
    {
        var grouped = query
            .Where(log => !(
                log.LifecycleStatus == ProxyRequestLifecycleStatus.Success
                || (log.LifecycleStatus == null && log.StatusCode != null && log.StatusCode < 400
                    && (log.Error == null || log.Error == ""))))
            .GroupBy(log => new
            {
                ChannelId = log.ChannelId,
                StatusCode = log.StatusCode ?? 0
            })
            .Select(group => new
            {
                ChannelId = group.Key.ChannelId,
                StatusCode = group.Key.StatusCode,
                Count = group.Count()
            })
            .OrderByDescending(item => item.Count)
            .Take(30)
            .ToList();
        if (grouped.Count == 0)
        {
            return [];
        }

        var channelIds = grouped
            .Where(item => item.ChannelId.HasValue)
            .Select(item => item.ChannelId!.Value)
            .Distinct()
            .ToList();
        var channelNames = ReadChannelNames(channelIds.Select(id => (Guid?)id));

        return grouped
            .Select(item =>
            {
                var channelName = item.ChannelId.HasValue
                    && channelNames.TryGetValue(item.ChannelId.Value, out var name)
                    ? name
                    : "未知渠道";
                return new ErrorDistributionDto(
                    item.ChannelId?.ToString() ?? "",
                    channelName,
                    item.StatusCode,
                    item.Count);
            })
            .ToList();
    }

    private static System.Linq.Expressions.Expression<Func<RequestLog, bool>> IsSuccessfulPredicate()
    {
        return log => log.LifecycleStatus == ProxyRequestLifecycleStatus.Success
            || (log.LifecycleStatus == null && log.StatusCode != null && log.StatusCode < 400
                && (log.Error == null || log.Error == ""));
    }

    private static RequestLogEventDto MapRequestLogEvent(
        RequestLogRow log,
        string ownerUsername,
        (int AttemptCount, int FailedAttemptCount) attemptStats)
    {
        var (attemptCount, failedAttemptCount) = attemptStats;
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
            NormalizeRequestStatus(log.LifecycleStatus, log.StatusCode, log.Error),
            log.ConversationKey,
            log.ConversationTurnId,
            log.ConversationWindowId,
            log.PreviousResponseId,
            attemptCount,
            failedAttemptCount,
            log.CostCurrency);
    }

    private Dictionary<Guid, (int AttemptCount, int FailedAttemptCount)> BuildAttemptStats(
        IReadOnlyList<RequestLogRow> logs)
    {
        var parentIds = logs
            .Where(log => log.RequestType == ProxyRequestTypes.Main)
            .Select(log => log.Id)
            .ToList();
        if (parentIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        return _logRepository.TableNoTracking
            .Where(log => log.RequestType == ProxyRequestTypes.Attempt && parentIds.Contains(log.ParentRequestLogId!.Value))
            .GroupBy(log => log.ParentRequestLogId!.Value)
            .Select(group => new
            {
                ParentId = group.Key,
                AttemptCount = group.Count(),
                FailedAttemptCount = group.Count(log =>
                    log.LifecycleStatus == ProxyRequestLifecycleStatus.Failed
                    || (log.StatusCode.HasValue && log.StatusCode.Value >= 400)
                    || !string.IsNullOrEmpty(log.Error))
            })
            .ToDictionary(item => item.ParentId, item => (item.AttemptCount, item.FailedAttemptCount));
    }

    private static RequestLogDto MapRequestLog(
        RequestLogRow log,
        LogContentSnapshot content,
        string ownerUsername,
        (int AttemptCount, int FailedAttemptCount) attemptStats)
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
            content.Get(RequestLogContentSlot.RequestHeaders),
            content.Get(RequestLogContentSlot.RequestBody),
            content.Get(RequestLogContentSlot.UpstreamRequestBody),
            content.Get(RequestLogContentSlot.UpstreamResponseBody),
            content.Get(RequestLogContentSlot.ResponseBody),
            content.Get(RequestLogContentSlot.WebSearchJson),
            content.Get(RequestLogContentSlot.OcrJson),
            ParseStreamLines(content.Get(RequestLogContentSlot.StreamLinesJson)),
            NormalizeRequestStatus(log.LifecycleStatus, log.StatusCode, log.Error),
            log.ConversationKey,
            log.ConversationTurnId,
            log.ConversationWindowId,
            log.PreviousResponseId,
            attemptStats.AttemptCount,
            attemptStats.FailedAttemptCount,
            log.CostCurrency);
    }

    private static IReadOnlyList<RequestLogStreamLineDto> ParseStreamLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Stored SSE log content must be a JSON array.");
        }

        var lines = new List<RequestLogStreamLineDto>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("sequence", out var sequence)
                || !sequence.TryGetInt32(out var sequenceValue)
                || !item.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("raw_line", out var rawLine)
                || rawLine.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Stored SSE log line is malformed.");
            }

            lines.Add(new RequestLogStreamLineDto(
                sequenceValue,
                source.GetString() ?? string.Empty,
                rawLine.GetString() ?? string.Empty));
        }

        return lines.OrderBy(line => line.Sequence).ToList();
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

    /// <summary>
    /// 日志列表/详情查询的显式投影，避免把 PricingSnapshotJson 等大字段读进内存。
    /// </summary>
    private sealed class RequestLogRow
    {
        public Guid Id { get; set; }

        public string? RequestId { get; set; }

        public double? CreatedAt { get; set; }

        public double? ProcessingStartedAt { get; set; }

        public double? CompletedAt { get; set; }

        public string? Method { get; set; }

        public string? Path { get; set; }

        public string? ClientIp { get; set; }

        public string? Model { get; set; }

        public string? UpstreamModel { get; set; }

        public Guid? ChannelId { get; set; }

        public string RequestType { get; set; } = string.Empty;

        public Guid? ParentRequestLogId { get; set; }

        public string? ConversationKey { get; set; }

        public string? ConversationTurnId { get; set; }

        public string? ConversationWindowId { get; set; }

        public string? PreviousResponseId { get; set; }

        public bool IsStream { get; set; }

        public int? TtftMs { get; set; }

        public int? DurationMs { get; set; }

        public int? StatusCode { get; set; }

        public int InputTokens { get; set; }

        public int CachedTokens { get; set; }

        public int OutputTokens { get; set; }

        public double Cost { get; set; }

        public string CostCurrency { get; set; } = string.Empty;

        public Guid OwnerUserId { get; set; }

        public Guid? ApiKeyId { get; set; }

        public string? Error { get; set; }

        public string? LifecycleStatus { get; set; }
    }

    /// <summary>
    /// 最近错误列表的显式投影，只取展示需要的列。
    /// </summary>
    private sealed class RecentErrorRow
    {
        public Guid Id { get; set; }

        public double? CreatedAt { get; set; }

        public string? Model { get; set; }

        public string? UpstreamModel { get; set; }

        public Guid? ChannelId { get; set; }

        public int? StatusCode { get; set; }

        public string? Error { get; set; }
    }
}
