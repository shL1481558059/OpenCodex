using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OpenCodex.Core.Protocols;

namespace OpenCodex.Api.Infrastructure;

/// <summary>Request-owned push adapter. One consumer/timer, bounded queue, and terminal flush acknowledgement.</summary>
public sealed class SsePushResponseWriter : IAsyncDisposable
{
    private sealed record Pending(SseFrame Frame, int Bytes, TaskCompletionSource? Flushed);
    private readonly HttpResponse _response;
    private readonly SseWriteOptions _options;
    private readonly Channel<Pending> _queue;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _emitGate = new(1);
    private readonly object _budgetGate = new();
    private TaskCompletionSource _budgetChanged = NewSignal();
    private TaskCompletionSource? _terminalAck;
    private ExceptionDispatchInfo? _failure;
    private readonly Task _worker;
    private long _queuedBytes;
    private bool _terminalCommitted;
    public bool TerminalCommitted => Volatile.Read(ref _terminalCommitted);
    public CancellationToken CancellationToken => _lifetime.Token;
    internal long QueuedBytes { get { lock (_budgetGate) return _queuedBytes; } }

    public SsePushResponseWriter(HttpResponse response, CancellationToken cancellationToken, SseWriteOptions? options = null)
    {
        _response = response;
        var configured = options ?? response.HttpContext.RequestServices?.GetService<IOptions<SseWriteOptions>>()?.Value ?? new();
        // Native/multi-agent HTTP historically ends at response.completed, without an added DONE.
        _options = new() { MaxBatchBytes = configured.MaxBatchBytes, MaxFlushDelayMilliseconds = configured.MaxFlushDelayMilliseconds,
            MaxEventBytes = configured.MaxEventBytes, MaxQueuedBytes = configured.MaxQueuedBytes,
            MaxQueuedEvents = configured.MaxQueuedEvents, AppendResponsesDone = false };
        _options.Validate();
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(_options.MaxQueuedEvents)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = RunAsync();
    }

    public async Task EmitAsync(Dictionary<string, object?> payload)
    {
        try
        {
            await _emitGate.WaitAsync(_lifetime.Token);
            try
            {
                _failure?.Throw();
                if (TerminalCommitted) throw new InvalidOperationException("Cannot publish another event after the terminal attempt.");
                var name = payload.GetValueOrDefault("type") as string ?? "";
                var frame = SseFrame.FromJson(payload, name);
                var bytes = frame.Utf8Length;
                if (bytes > _options.MaxEventBytes) throw new InvalidDataException("SSE event exceeds configured MaxEventBytes; output was not truncated.");
                if (!_response.HasStarted) ProxyStreamResponseWriter.PrepareSse(_response);
                TaskCompletionSource? ack = null;
                if (frame.IsTerminal)
                {
                    Volatile.Write(ref _terminalCommitted, true); // Before any terminal bytes may escape.
                    ack = _terminalAck = NewSignal();
                }
                await ReserveAsync(bytes);
                try { await _queue.Writer.WriteAsync(new(frame, bytes, ack), _lifetime.Token); }
                catch { Release(bytes); throw; }
                // Acceptance into the queue is NOT terminal success: wait for the actual flush.
                if (ack is not null) await ack.Task.WaitAsync(_lifetime.Token);
            }
            finally { _emitGate.Release(); }
        }
        catch (OperationCanceledException) { _failure?.Throw(); throw; }
        catch (ChannelClosedException) { _failure?.Throw(); throw; }
    }

    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
    }

    private async Task RunAsync()
    {
        try
        {
            await ProxyStreamResponseWriter.WriteFramesAsync(_response, ReadAsync(), SseFramePredicate.Never, static () => 0,
                _lifetime.Token, _options);
        }
        catch (Exception error)
        {
            _failure = ExceptionDispatchInfo.Capture(error);
            _terminalAck?.TrySetException(error);
            _queue.Writer.TryComplete(error);
            _lifetime.Cancel();
            throw;
        }
        finally
        {
            while (_queue.Reader.TryRead(out var pending)) Release(pending.Bytes);
        }
    }

    private async IAsyncEnumerable<SseFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var pending in _queue.Reader.ReadAllAsync(ct))
        {
            var accepted = false;
            try { yield return pending.Frame; accepted = true; }
            finally
            {
                Release(pending.Bytes);
                // The writer resumes the iterator only AFTER terminal FlushAsync returned.
                // Disposal following a failed flush must never acknowledge success.
                if (accepted) pending.Flushed?.TrySetResult();
            }
        }
    }

    private async Task ReserveAsync(int bytes)
    {
        while (true)
        {
            Task changed;
            lock (_budgetGate)
            {
                // A single event may exceed the batch/queue budget, never MaxEventBytes.
                // It must be alone. Queued/in-flight wire weight is bounded by max of
                // these limits; at most one producer-owned pending event is outside it.
                if (_queuedBytes == 0 || bytes <= _options.MaxQueuedBytes - _queuedBytes)
                {
                    _queuedBytes += bytes;
                    return;
                }
                changed = _budgetChanged.Task;
            }
            await changed.WaitAsync(_lifetime.Token);
        }
    }

    private void Release(int bytes)
    {
        TaskCompletionSource changed;
        lock (_budgetGate) { _queuedBytes -= bytes; changed = _budgetChanged; _budgetChanged = NewSignal(); }
        changed.TrySetResult();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        try { await _worker; } catch { /* CompleteAsync/EmitAsync own delivery errors; disposal never masks them. */ }
        _lifetime.Dispose();
        _emitGate.Dispose();
    }
}
