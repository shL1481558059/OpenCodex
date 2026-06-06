using System.Collections.Concurrent;

namespace OpenCodex.Api.Persistence;

public sealed class AsyncRequestLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _dbPath;
    private readonly string _defaultOwnerUsername;
    private readonly BlockingCollection<IReadOnlyDictionary<string, object?>?> _queue = new();
    private Thread? _worker;
    private bool _running;
    private bool _disposed;

    public AsyncRequestLogWriter(string dbPath, string defaultOwnerUsername = "admin")
    {
        _dbPath = dbPath;
        _defaultOwnerUsername = string.IsNullOrWhiteSpace(defaultOwnerUsername)
            ? "admin"
            : defaultOwnerUsername.Trim();
        OpenCodexDatabase.Initialize(_dbPath, _defaultOwnerUsername);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _running)
            {
                return;
            }

            _running = true;
            _worker = new Thread(Worker)
            {
                IsBackground = true,
                Name = "OpenCodex request log writer"
            };
            _worker.Start();
        }
    }

    public void Stop()
    {
        Stop(TimeSpan.FromSeconds(5));
    }

    public void Stop(TimeSpan timeout)
    {
        Thread? worker;
        lock (_sync)
        {
            if (_disposed || !_running)
            {
                return;
            }

            _running = false;
            worker = _worker;
            _queue.Add(null);
        }

        worker?.Join(timeout);

        lock (_sync)
        {
            if (ReferenceEquals(worker, _worker))
            {
                _worker = null;
            }
        }
    }

    public void Write(IReadOnlyDictionary<string, object?> record)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _queue.Add(record.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Dispose();
        }
    }

    private void Worker()
    {
        foreach (var record in _queue.GetConsumingEnumerable())
        {
            if (record is null)
            {
                break;
            }

            try
            {
                OpenCodexDatabase.WriteRequestLog(_dbPath, record, _defaultOwnerUsername);
            }
            catch
            {
                // Match Python AsyncDBWriter: failed background writes are dropped.
            }
        }
    }
}
