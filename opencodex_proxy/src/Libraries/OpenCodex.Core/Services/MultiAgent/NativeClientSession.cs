namespace OpenCodex.Core.Services.MultiAgent;

/// <summary>Identity and request coordination for a client-owned conversation.</summary>
public sealed class NativeClientSession
{
    public Guid Owner { get; internal init; }
    public string ThreadId { get; internal init; } = "";
    public string RootThreadId { get; internal init; } = "";
    public string ParentThreadId { get; internal init; } = "";
    public string AgentName { get; internal init; } = "";
    public bool Persist { get; internal init; }
    public int MaxConcurrentSubagents { get; internal init; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal SemaphoreSlim ChildModels { get; init; } = null!;
    internal object ActivityLock { get; } = new();
    internal NativeClientRequestLease? Activity { get; set; }
}

/// <summary>A single Responses request projection; it never schedules business tasks.</summary>
public sealed class NativeClientRequest
{
    public Dictionary<string, object?> Payload { get; internal init; } = [];
    public Dictionary<string, object?>? ReplayResponse { get; internal init; }
    internal NativeClientSession Session { get; init; } = null!;
    internal string ReplayKey { get; init; } = "";
}

/// <summary>Owns one active model request and releases its thread and child permits.</summary>
public sealed class NativeClientRequestLease : IAsyncDisposable
{
    private readonly NativeClientSession _session;
    private readonly CancellationTokenSource _stop;
    private bool _childPermit;
    private int _disposed;

    internal NativeClientRequestLease(NativeClientSession session, CancellationToken ct)
    {
        _session = session;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Token = _stop.Token;
    }

    internal void MarkChildPermitAcquired() => _childPermit = true;

    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    public CancellationToken Token { get; }

    public Task CancelAsync()
    {
        lock (_session.ActivityLock)
            return _disposed != 0 ? Task.CompletedTask : _stop.CancelAsync();
    }

    public ValueTask DisposeAsync()
    {
        lock (_session.ActivityLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            if (ReferenceEquals(_session.Activity, this)) _session.Activity = null;
            _stop.Dispose();
        }
        if (_childPermit) _session.ChildModels.Release();
        _session.Gate.Release();
        return ValueTask.CompletedTask;
    }
}
