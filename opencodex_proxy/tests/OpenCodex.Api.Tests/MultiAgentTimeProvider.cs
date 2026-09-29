using System.Threading.Channels;

namespace OpenCodex.Api.Tests;

internal sealed class MultiAgentTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Channel<bool> _created = Channel.CreateUnbounded<bool>();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        _created.Writer.TryWrite(true);
        return timer;
    }

    public async Task WaitForTimerAsync(CancellationToken ct) => await _created.Reader.ReadAsync(ct);

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += elapsed;
            due = _timers.Where(t => t.Due is not null && t.Due <= _now).ToList();
            foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(MultiAgentTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period;
                return true;
            }
        }

        public void Fire() { lock (owner._gate) { if (_disposed) return; } callback(state); }
        public void Dispose() { lock (owner._gate) { _disposed = true; Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
