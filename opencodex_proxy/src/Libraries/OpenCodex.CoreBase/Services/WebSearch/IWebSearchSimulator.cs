namespace OpenCodex.CoreBase.Services.WebSearch;

public interface IWebSearchSimulator
{
    bool CanSimulate(
        string entryProtocol,
        string channelType,
        string ownerRole,
        IReadOnlyDictionary<string, object?> payload);

    Task<WebSearchSimulationResult> RunAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> RunChatStreamAsync(
        IReadOnlyDictionary<string, object?> channel,
        Dictionary<string, object?> upstreamRequest,
        Dictionary<string, object?> payload,
        string? originalModel,
        int defaultTimeout,
        WebSearchStreamResult result,
        CancellationToken cancellationToken);
}
