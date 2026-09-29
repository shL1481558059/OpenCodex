namespace OpenCodex.Core.Services.MultiAgent;

public delegate Task<Dictionary<string, object?>> MultiAgentModelCall(
    Dictionary<string, object?> payload,
    Func<Dictionary<string, object?>, CancellationToken, Task> onEvent,
    CancellationToken ct);
