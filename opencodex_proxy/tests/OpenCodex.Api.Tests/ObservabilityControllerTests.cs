using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Controllers;
using OpenCodex.Core.Services.Events;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Observability;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ObservabilityControllerTests
{
    [Fact]
    public void Logs_ForwardsConversationKeyFilter()
    {
        var service = new CapturingObservabilityService();
        var controller = CreateController(service);

        controller.Logs(
            request_id: null,
            conversation_key: "thread:abc",
            model: null,
            upstream_model: null,
            channel_id: null,
            owner_username: null,
            api_key_id: null,
            path: null,
            request_type: null,
            status_code: null,
            is_stream: null,
            client_ip: null,
            error: null,
            request_status: null,
            created_from: null,
            created_to: null);

        Assert.Equal("thread:abc", service.LastFilters!["conversation_key"]);
    }

    [Fact]
    public void Logs_ForwardsBranchNavigationFilters()
    {
        var service = new CapturingObservabilityService();
        var controller = CreateController(service);

        controller.Logs(
            request_id: null,
            model: null,
            upstream_model: null,
            channel_id: null,
            owner_username: null,
            api_key_id: null,
            path: null,
            request_type: null,
            status_code: null,
            is_stream: null,
            client_ip: null,
            error: null,
            request_status: null,
            created_from: null,
            created_to: null,
            conversation_turn_id: "turn-7",
            conversation_window_id: "window-2",
            previous_response_id: "resp-parent");

        Assert.Equal("turn-7", service.LastFilters!["conversation_turn_id"]);
        Assert.Equal("window-2", service.LastFilters["conversation_window_id"]);
        Assert.Equal("resp-parent", service.LastFilters["previous_response_id"]);
    }

    [Fact]
    public void LogFilterOptions_ExcludesCurrentConversationKeyFromDependentFilters()
    {
        var service = new CapturingObservabilityService();
        var controller = CreateController(service);

        controller.LogFilterOptions(
            field: "conversation_key",
            q: "thread",
            conversation_key: "thread:abc");

        Assert.NotNull(service.LastFilters);
        Assert.DoesNotContain("conversation_key", service.LastFilters!);
    }

    [Fact]
    public void Stats_ForwardsConversationKeyFilter()
    {
        var service = new CapturingObservabilityService();
        var controller = CreateController(service);

        controller.Stats(conversation_key: "thread:abc");

        Assert.Equal("thread:abc", service.LastFilters!["conversation_key"]);
    }

    private static ObservabilityController CreateController(CapturingObservabilityService service)
    {
        return new ObservabilityController(new TestWorkContext(), service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class CapturingObservabilityService : IObservabilityService
    {
        public IReadOnlyDictionary<string, object?>? LastFilters { get; private set; }

        public ApiOpResult<LogsPageResponse> ReadLogsPage(object? page, object? pageSize, IReadOnlyDictionary<string, object?> filters)
        {
            LastFilters = filters;
            return ApiOpResult<LogsPageResponse>.Fail(500, "captured");
        }

        public ApiOpResult<IReadOnlyDictionary<string, object>> ReadLogFilterOption(string field, object? query, IReadOnlyDictionary<string, object?> filters)
        {
            LastFilters = filters;
            return ApiOpResult<IReadOnlyDictionary<string, object>>.Fail(500, "captured");
        }

        public ApiOpResult<LogDetailResponse> ReadLogById(Guid logId) => throw new NotSupportedException();
        public ApiOpResult<StatsResponse> ReadStats(string rangeKey, object? startTs, object? endTs, IReadOnlyDictionary<string, object?> filters)
        {
            LastFilters = filters;
            return ApiOpResult<StatsResponse>.Fail(500, "captured");
        }
        public ApiOpResult<ActiveChannelQueueResponse> ReadActiveChannelQueue() => throw new NotSupportedException();
        public ApiOpResult<IReadOnlyList<RecentErrorItemResponse>> ReadRecentErrors(int limit) => throw new NotSupportedException();
        public ApiOpResult<ClearLogsResponse> ClearLogs() => throw new NotSupportedException();

        public ApiOpResult<StatsSummaryResponse> ReadStatsSummary(string rangeKey, object? startTs, object? endTs, IReadOnlyDictionary<string, object?> filters)
        {
            LastFilters = filters;
            return ApiOpResult<StatsSummaryResponse>.Fail(500, "captured");
        }

        public ApiOpResult<IReadOnlyList<StatsPointResponse>> ReadStatsTimeseries(string rangeKey, object? startTs, object? endTs, IReadOnlyDictionary<string, object?> filters) => throw new NotSupportedException();
        public ApiOpResult<IReadOnlyList<StatsModelDistributionResponse>> ReadStatsModelDistribution(string rangeKey, object? startTs, object? endTs, IReadOnlyDictionary<string, object?> filters) => throw new NotSupportedException();
        public ApiOpResult<IReadOnlyList<ErrorDistributionResponse>> ReadStatsErrorDistribution(string rangeKey, object? startTs, object? endTs, IReadOnlyDictionary<string, object?> filters) => throw new NotSupportedException();
    }

    private sealed class TestWorkContext : IWorkContext
    {
        private static readonly SessionUser User = new(Guid.NewGuid(), "admin", "superadmin", true);
        public SessionUser? CurrentUser => User;
        public bool IsSignedIn => true;
        public bool IsSuperadmin => true;
        public SessionUser RequireUser() => User;
        public SessionUser RequireSuperadmin() => User;
    }
}
