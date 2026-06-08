using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Ef;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.WebSearch;

public sealed partial class WebSearchSimulator : IWebSearchSimulator
{
    private const string WebSearchToolName = WebSearchRequestPolicy.ToolName;

    private readonly IUpstreamClient _upstream;
    private readonly IWebSearchClient _webSearchClient;
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public WebSearchSimulator(
        IUpstreamClient upstream,
        IWebSearchClient webSearchClient,
        IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _upstream = upstream;
        _webSearchClient = webSearchClient;
        _settingsProvider = settingsProvider;
    }

    public bool CanSimulate(
        string entryProtocol,
        string channelType,
        string ownerRole,
        IReadOnlyDictionary<string, object?> payload)
    {
        return entryProtocol == ProtocolConverter.Responses
            && channelType is ProtocolConverter.Chat or ProtocolConverter.Messages
            && string.Equals(ownerRole, "superadmin", StringComparison.Ordinal)
            && WebSearchRequestPolicy.DeclaresWebSearchTool(payload)
            && WebSearchEnabled();
    }

    private async Task<Dictionary<string, object?>> PostUpstream(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> requestPayload,
        int defaultTimeout,
        IReadOnlyList<WebSearchToolResult> webResults,
        IReadOnlyList<Dictionary<string, object?>> upstreamCalls,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _upstream.PostJsonAsync(channel, requestPayload, defaultTimeout, cancellationToken);
        }
        catch (ProxyException exception)
        {
            var details = WebSearchSimulationLog.Build(webResults, upstreamCalls);
            details["upstream_error"] = exception.Message;
            throw new WebSearchSimulationUpstreamException(exception, requestPayload, details);
        }
    }

    private bool WebSearchEnabled()
    {
        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        return context.WebSearchSettings
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == 1)
            ?.Enabled ?? false;
    }

    private TavilyKeyDto? ReserveTavilyKey()
    {
        var settings = _settingsProvider.GetSettings();
        EfServiceSupport.InitializeDatabase(settings.DbPath, settings.AdminUsername);
        using var context = OpenCodexDbContextFactory.Create(settings.DbPath);
        using var transaction = context.Database.BeginTransaction();
        var reserved = context.TavilyKeys
            .Where(key => key.Enabled && key.UsageCount < key.UsageLimit)
            .OrderBy(key => key.Position)
            .ThenBy(key => key.Id)
            .FirstOrDefault();
        if (reserved is null)
        {
            transaction.Rollback();
            return null;
        }

        reserved.UsageCount += 1;
        reserved.UpdatedAt = EfServiceSupport.UnixTimeSeconds();
        context.SaveChanges();
        transaction.Commit();
        return EfServiceSupport.ToTavilyKeyDto(reserved);
    }
}
