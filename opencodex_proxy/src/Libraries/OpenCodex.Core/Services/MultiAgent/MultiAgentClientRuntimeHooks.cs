using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Connects one native client agent to its server-side context and coordination registry.</summary>
public sealed class MultiAgentClientRuntimeHooks
{
    public string AgentName { get; init; } = "/root";
    public string ParentName { get; init; } = "";
    public MultiAgentClientTools Tools { get; init; } = MultiAgentClientTools.FromDefinitions([]);
    public Func<MultiAgentState, D, List<object?>, CancellationToken, Task>? BeforeToolCall { get; init; }
    public Func<IReadOnlyList<D>, CancellationToken, Task>? RollbackToolCalls { get; init; }
    public Func<D, D, CancellationToken, Task>? ToolResult { get; init; }
}
