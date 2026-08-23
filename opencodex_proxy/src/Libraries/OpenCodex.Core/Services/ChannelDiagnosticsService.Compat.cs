using OpenCodex.Core.Services.Proxy;

namespace OpenCodex.Core.Services;

public sealed partial class ChannelDiagnosticsService
{
    private static Dictionary<string, object?> ApplyCompat(
        IReadOnlyDictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> compat)
    {
        return ChannelCompatRequestRewriter.Apply(payload, compat).Payload;
    }
}
