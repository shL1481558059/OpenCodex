using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>One owner's root group owns its shared policy and canonical agent identities.</summary>
internal sealed class NativeClientGroup(bool persist, int limit)
{
    public bool Persist { get; } = persist;
    public int Limit { get; } = limit;
    public SemaphoreSlim ChildModels { get; } = new(limit, limit);
    public Dictionary<string, string> Agents { get; } = new(StringComparer.Ordinal);

    public void Validate(MultiAgentClientIdentity identity, bool persist, int limit)
    {
        if (persist != Persist)
            throw new BadRequestException("Client threads must keep their group's initial storage policy.");
        if (identity.ParentThreadId.Length == 0 && limit != Limit)
            throw new BadRequestException("The root must keep its group's initial max_concurrent_subagents.");
        if (Agents.TryGetValue(identity.AgentName, out var thread) && thread != identity.ThreadId)
            throw new BadRequestException("This agent identity is already attached to another client thread.");
    }
}
