namespace OpenCodex.Api.Services;

public sealed class AdminChannelTestResult
{
    public AdminChannelTestResult(
        string model,
        string upstreamModel,
        IReadOnlyList<string> compat,
        Dictionary<string, object?> response)
    {
        Model = model;
        UpstreamModel = upstreamModel;
        Compat = compat;
        Response = response;
    }

    public string Model { get; }

    public string UpstreamModel { get; }

    public IReadOnlyList<string> Compat { get; }

    public Dictionary<string, object?> Response { get; }
}
