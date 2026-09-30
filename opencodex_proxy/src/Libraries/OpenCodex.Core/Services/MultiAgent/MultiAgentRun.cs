namespace OpenCodex.Core.Services.MultiAgent;

public sealed class MultiAgentInjection
{
    public string ResponseId { get; set; } = "";
    public List<object?> Input { get; set; } = [];
}

public sealed class MultiAgentRun
{
    public HashSet<string> SeenUserMessages { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public Dictionary<string, object?> Template { get; set; } = new(StringComparer.Ordinal);
    public List<object?> ClientTools { get; set; } = [];
    public Dictionary<string, Dictionary<string, object?>> ClientControlCalls { get; set; } = new(StringComparer.Ordinal);
    public List<object?>? InstructionMessages { get; set; }
    public Dictionary<string, MultiAgentState> Agents { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PendingCalls { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> ReceivedCalls { get; set; } = new(StringComparer.Ordinal);
    public List<object?> OutputHistory { get; set; } = [];
    public int MaxConcurrentSubagents { get; set; } = 3;
    public int ModelTurns { get; set; }
    public int CompactThresholdTokens { get; set; } = 64000;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool Finished { get; set; }
    public int ClientInputCount { get; set; }
    public string LastResponseId { get; set; } = string.Empty;
    public HashSet<string> ResponseIds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MultiAgentState
{
    public bool TaskStarted { get; set; }
    public List<object?> CurrentTurnPrefix { get; set; } = [];
    public List<MultiAgentPendingTask> PendingTasks { get; set; } = [];
    public List<MultiAgentTaskTurn> CompletedTurns { get; set; } = [];
    public int CurrentTurnStart { get; set; }
    public int CurrentTaskGeneration { get; set; }
    public string? LastError { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string Status { get; set; } = "ready";
    public List<object?> History { get; set; } = [];
    public List<object?> Mailbox { get; set; } = [];
    public string LastTaskMessage { get; set; } = string.Empty;
    public int Generation { get; set; }
    public int LastInputTokens { get; set; }
    public string? WaitingCallId { get; set; }
    public int WaitTimeoutMs { get; set; } = 10000;
    public DateTimeOffset? WaitUntil { get; set; }
}

public sealed class MultiAgentPendingTask
{
    public int Generation { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<object?> Messages { get; set; } = [];
}

public sealed class MultiAgentTaskTurn
{
    public int Generation { get; set; }
    public string Status { get; set; } = "completed";
    public List<object?> Items { get; set; } = [];
}
