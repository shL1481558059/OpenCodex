namespace OpenCodex.Api.Routing;

public sealed class RouteResult
{
    public RouteResult(
        Dictionary<string, object?> channel,
        string originalModel,
        string upstreamModel)
    {
        Channel = channel;
        OriginalModel = originalModel;
        UpstreamModel = upstreamModel;
    }

    public Dictionary<string, object?> Channel { get; }

    public string OriginalModel { get; }

    public string UpstreamModel { get; }
}
