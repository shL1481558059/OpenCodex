namespace OpenCodex.Api.Abstractions;

public interface IUpstreamClient
{
    Task<Dictionary<string, object?>> PostJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamJsonAsync(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        int defaultTimeout,
        CancellationToken cancellationToken);
}

public interface IUpstreamModelClient
{
    Task<Dictionary<string, object?>> ListModelsAsync(
        IReadOnlyDictionary<string, object?> channel,
        int defaultTimeout,
        CancellationToken cancellationToken);
}
