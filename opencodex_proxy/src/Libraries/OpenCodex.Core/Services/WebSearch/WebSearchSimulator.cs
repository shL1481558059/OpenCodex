using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.WebSearch;

public sealed partial class WebSearchSimulator : IWebSearchSimulator
{
    private const string WebSearchToolName = WebSearchRequestPolicy.ToolName;

    private readonly IUpstreamClient _upstream;
    private readonly IWebSearchClient _webSearchClient;
    private readonly IRepository<WebSearchSettings> _settingsRepository;
    private readonly IRepository<TavilyKey> _keyRepository;

    public WebSearchSimulator(
        IUpstreamClient upstream,
        IWebSearchClient webSearchClient,
        IRepository<WebSearchSettings> settingsRepository,
        IRepository<TavilyKey> keyRepository)
    {
        _upstream = upstream;
        _webSearchClient = webSearchClient;
        _settingsRepository = settingsRepository;
        _keyRepository = keyRepository;
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
        return _settingsRepository.TableNoTracking.FirstOrDefault()?.Enabled ?? false;
    }

    private TavilyKeyDto? ReserveTavilyKey()
    {
        var reserved = _keyRepository.Table
            .Where(key => key.Enabled && key.UsageCount < key.UsageLimit)
            .OrderBy(key => key.Position)
            .ThenBy(key => key.Id)
            .FirstOrDefault();
        if (reserved is null)
        {
            return null;
        }

        reserved.UsageCount += 1;
        reserved.UpdatedAt = UnixTimeSeconds();
        _keyRepository.Update(reserved);
        return MapToDto(reserved);
    }

    private static TavilyKeyDto MapToDto(TavilyKey key)
    {
        return new TavilyKeyDto(
            key.Id,
            key.Position,
            key.Provider,
            key.ApiKey,
            key.Enabled,
            key.UsageCount,
            key.UsageLimit,
            key.UsageLimit);
    }

    private static double UnixTimeSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
