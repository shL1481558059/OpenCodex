namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>A client thread transports one server actor. Runtime state never includes its siblings.</summary>
public sealed class MultiAgentClientBinding
{
    public string Id { get; internal set; } = string.Empty;
    public Guid Owner { get; internal set; }
    public string RootThreadId { get; internal set; } = string.Empty;
    public string ThreadId { get; internal set; } = string.Empty;
    public string AgentName { get; internal set; } = string.Empty;
    public string ParentName { get; internal set; } = string.Empty;
    public string ParentThreadId { get; internal set; } = string.Empty;
    public string SpawnCallId { get; internal set; } = string.Empty;
    public bool SpawnFailed { get; internal set; }
    public bool SpawnCompleted { get; internal set; }
    public MultiAgentRun Run { get; internal set; } = null!;
    public bool Persist { get; internal set; }
    public CancellationTokenSource Stop { get; } = new();
}

/// <summary>A request-scoped cancellation registration; cancelling it does not prevent followup work.</summary>
public sealed class MultiAgentClientActivity : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop;
    private readonly Action<MultiAgentClientActivity> _completed;
    private bool _disposed;

    internal MultiAgentClientActivity(CancellationToken request, CancellationToken binding,
        Action<MultiAgentClientActivity> completed)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(request, binding);
        Token = _stop.Token;
        _completed = completed;
    }

    public CancellationToken Token { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N");

    internal Task CancelAsync()
    {
        lock (_sync) return _disposed ? Task.CompletedTask : _stop.CancelAsync();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Dispose();
        }
        _completed(this);
    }
}
