using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Tests.Services.Admin;

public sealed class AdminObservabilityServiceTests
{
    [Fact]
    public void RegularUserLogsPageForcesOwnerFilterToSelf()
    {
        var observability = new FakeAdminObservabilityRepository();
        var service = new AdminObservabilityService(observability);

        var result = service.ReadLogsPage(
            "2",
            "25",
            new Dictionary<string, object?>
            {
                ["model"] = "gpt-4o",
                ["owner_username"] = "bob"
            },
            "alice",
            isSuperadmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        var call = Assert.Single(observability.LogsPageCalls);
        Assert.Equal("2", call.Page);
        Assert.Equal("25", call.PageSize);
        Assert.Equal("gpt-4o", call.Filters["model"]);
        Assert.Equal("alice", call.Filters["owner_username"]);
    }

    [Fact]
    public void SuperadminLogsPagePreservesRequestedOwnerFilter()
    {
        var observability = new FakeAdminObservabilityRepository();
        var service = new AdminObservabilityService(observability);

        var result = service.ReadLogsPage(
            "1",
            "50",
            new Dictionary<string, object?>
            {
                ["owner_username"] = "bob"
            },
            "admin",
            isSuperadmin: true);

        Assert.True(result.Succeeded);
        var call = Assert.Single(observability.LogsPageCalls);
        Assert.Equal("bob", call.Filters["owner_username"]);
    }

    [Fact]
    public void RegularUserFilterOptionsForcesOwnerFilterToSelf()
    {
        var observability = new FakeAdminObservabilityRepository();
        var service = new AdminObservabilityService(observability);

        var result = service.ReadLogFilterOption(
            "model",
            "gpt",
            new Dictionary<string, object?> { ["owner_username"] = "bob" },
            "alice",
            isSuperadmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        var call = Assert.Single(observability.FilterOptionCalls);
        Assert.Equal("model", call.Field);
        Assert.Equal("gpt", call.Query);
        Assert.Equal("alice", call.Filters["owner_username"]);
    }

    [Fact]
    public void ReadLogByIdMapsMissingLogToNotFound()
    {
        var observability = new FakeAdminObservabilityRepository();
        var service = new AdminObservabilityService(observability);

        var result = service.ReadLogById(123, "alice", isSuperadmin: false);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Equal(AdminObservabilityErrorCodes.NotFound, result.Code);
        Assert.Equal("log not found", result.Message);
        var call = Assert.Single(observability.LogDetailCalls);
        Assert.Equal(123, call.LogId);
        Assert.Equal("alice", call.Filters["owner_username"]);
    }

    [Fact]
    public void ReadStatsScopesRegularUserAndLeavesSuperadminUnscoped()
    {
        var observability = new FakeAdminObservabilityRepository();
        var service = new AdminObservabilityService(observability);

        var regular = service.ReadStats("custom", "1", "2", "alice", isSuperadmin: false);
        var admin = service.ReadStats("1h", null, null, "admin", isSuperadmin: true);

        Assert.True(regular.Succeeded);
        Assert.True(admin.Succeeded);
        Assert.Equal(
            [("custom", (object?)"1", (object?)"2", (string?)"alice"), ("1h", null, null, null)],
            observability.StatsCalls);
    }

    private static RequestLogRecord LogRecord(long id = 1)
    {
        return new RequestLogRecord(
            id,
            "req",
            1,
            "POST",
            "/v1/responses",
            "127.0.0.1",
            "gpt-4o",
            "gpt-4o",
            "openai",
            false,
            null,
            10,
            200,
            1,
            0,
            1,
            0,
            "alice",
            null,
            null,
            "{}",
            "{}",
            "{}",
            "{}",
            "{}",
            null,
            "success");
    }

    private static StatsRecord Stats()
    {
        return new StatsRecord(
            "1h",
            "1970-01-01T00:00:00Z",
            "1970-01-01T01:00:00Z",
            1,
            7.25,
            new StatsSummaryRecord(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            [],
            []);
    }

    private sealed class FakeAdminObservabilityRepository : IAdminObservabilityRepository
    {
        public RequestLogRecord? Log { get; init; }

        public List<(object? Page, object? PageSize, IReadOnlyDictionary<string, object?> Filters)> LogsPageCalls { get; } = [];

        public List<(string Field, object? Query, IReadOnlyDictionary<string, object?> Filters)> FilterOptionCalls { get; } = [];

        public List<(long LogId, IReadOnlyDictionary<string, object?> Filters)> LogDetailCalls { get; } = [];

        public List<(string RangeKey, object? StartTs, object? EndTs, string? OwnerUsername)> StatsCalls { get; } = [];

        public RequestLogPageRecord ReadLogsPage(
            object? page,
            object? pageSize,
            IReadOnlyDictionary<string, object?> filters)
        {
            LogsPageCalls.Add((page, pageSize, Copy(filters)));
            return new RequestLogPageRecord([], 0, 1, 50);
        }

        public IReadOnlyDictionary<string, object> ReadLogFilterOption(
            string field,
            object? query,
            IReadOnlyDictionary<string, object?> filters)
        {
            FilterOptionCalls.Add((field, query, Copy(filters)));
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public RequestLogRecord? ReadLogById(
            long logId,
            IReadOnlyDictionary<string, object?> filters)
        {
            LogDetailCalls.Add((logId, Copy(filters)));
            return Log;
        }

        public StatsRecord ReadStats(
            string rangeKey,
            object? startTs,
            object? endTs,
            string? ownerUsername)
        {
            StatsCalls.Add((rangeKey, startTs, endTs, ownerUsername));
            return Stats();
        }

        private static IReadOnlyDictionary<string, object?> Copy(
            IReadOnlyDictionary<string, object?> filters)
        {
            return filters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }
    }
}
