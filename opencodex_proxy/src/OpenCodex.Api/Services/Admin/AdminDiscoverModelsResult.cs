namespace OpenCodex.Api.Services;

public sealed class AdminDiscoverModelsResult
{
    public AdminDiscoverModelsResult(
        IReadOnlyList<string> models,
        Dictionary<string, object?> raw)
    {
        Models = models;
        Raw = raw;
    }

    public IReadOnlyList<string> Models { get; }

    public Dictionary<string, object?> Raw { get; }
}
