namespace OpenCodex.CoreBase.Domain.Proxy;

public sealed class BuiltinToolRequestContext
{
    public required string WebSearchToolName { get; init; }

    public int MaxWebSearchCalls { get; init; } = 15;

    public bool SearchAllowed { get; init; } = true;

    public bool IncludeSources { get; init; }

    public IReadOnlySet<string>? AllowedToolNames { get; init; }

    public bool HasExecuted { get; set; }
}
