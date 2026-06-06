using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.DTOs.Admin;
using OpenCodex.Api.DTOs.AdminObservability;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class AdminObservabilityController : AdminApiControllerBase
{
    private readonly IAdminObservabilityService _observability;

    public AdminObservabilityController(
        IAdminSessionService adminSession,
        IAdminObservabilityService observability)
        : base(adminSession)
    {
        _observability = observability;
    }

    [HttpGet("/admin/api/logs")]
    [ProducesResponseType(typeof(LogsPageResponse), StatusCodes.Status200OK)]
    public IActionResult Logs()
    {
        var user = RequireUser();
        var filters = LogFilterQuery.FromQuery(QueryValues());
        var result = _observability.ReadLogsPage(
            QueryValue("page") ?? "1",
            QueryValue("page_size") ?? "50",
            filters,
            user.Username,
            AdminSession.IsSuperadmin(user));
        var page = result.Data!;

        return Ok(LogsPageResponse.From(page));
    }

    [HttpGet("/admin/api/log-filter-options")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public IActionResult LogFilterOptions()
    {
        var user = RequireUser();
        var field = QueryValue("field") ?? string.Empty;
        var filters = LogFilterQuery.FromQuery(QueryValues(), excludedKey: field);
        var result = _observability.ReadLogFilterOption(
            field,
            QueryValue("q") ?? string.Empty,
            filters,
            user.Username,
            AdminSession.IsSuperadmin(user));
        return Ok(result.Data);
    }

    [HttpGet("/admin/api/logs/{logId:long}")]
    [ProducesResponseType(typeof(LogDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound)]
    public IActionResult LogDetail(long logId)
    {
        var user = RequireUser();
        var result = _observability.ReadLogById(
            logId,
            user.Username,
            AdminSession.IsSuperadmin(user));
        if (!result.Succeeded || result.Data is null)
        {
            return NotFoundError(result.Message);
        }

        return Ok(LogDetailResponse.From(result.Data));
    }

    [HttpGet("/admin/api/stats")]
    [ProducesResponseType(typeof(StatsResponse), StatusCodes.Status200OK)]
    public IActionResult Stats()
    {
        var user = RequireUser();
        var result = _observability.ReadStats(
            QueryValue("range") ?? "1h",
            QueryValue("start"),
            QueryValue("end"),
            user.Username,
            AdminSession.IsSuperadmin(user));
        var stats = result.Data!;

        return Ok(StatsResponse.From(stats));
    }

    private IEnumerable<KeyValuePair<string, string?>> QueryValues()
    {
        foreach (var (key, values) in Request.Query)
        {
            yield return new KeyValuePair<string, string?>(key, values.ToString());
        }
    }
}
