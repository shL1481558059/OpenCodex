using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace OpenCodex.Core.Services.Caching;

/// <summary>
/// 基于 <see cref="ConnectionMultiplexer"/> 的 Redis 连接提供者。
/// </summary>
/// <remarks>
/// 懒加载 + 双检锁单例,参考 shl_file_server 的连接配置:
/// <c>AbortOnConnectFail=false</c>、<c>ConnectRetry=3</c>、<c>ExponentialRetry(5000)</c>。
/// 连接建立与重连都在后台完成,调用路径上不做网络 IO:连接未就绪、已断开或正在重连时
/// 立即返回 null,由调用方降级为进程内实现,避免把线程池线程耗在等待 Redis 上。
/// 连接串为空时直接禁用(<see cref="IsAvailable"/>=false);首次连接失败时标记不可用并降级,
/// 不抛异常、不阻塞服务启动。key 前缀通过 <c>WithKeyPrefix</c> 统一挂载。
/// </remarks>
public sealed class RedisConnectionProvider : IRedisConnectionProvider, IDisposable
{
    /// <summary>
    /// 单条命令的最长等待时间。连接假死(端口可达但不响应)时命令最多排队这么久。
    /// </summary>
    private const int CommandTimeoutMilliseconds = 1000;

    /// <summary>
    /// 构造期的连接等待上限。构造发生在进程启动阶段而非请求路径上,
    /// 这里短暂等待可以让依赖构造期订阅 Redis 的组件(如事件总线)拿到连接。
    /// </summary>
    private static readonly TimeSpan StartupConnectWait = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly object _lock = new();
    private volatile ConnectionMultiplexer? _connection;
    private volatile bool _connectionFailed;
    private volatile bool _disposed;
    private Task? _connectTask;

    public RedisConnectionProvider(string? connectionString, string? keyPrefix)
    {
        _connectionString = (connectionString ?? string.Empty).Trim();
        KeyPrefix = string.IsNullOrWhiteSpace(keyPrefix) ? "opencodex" : keyPrefix.Trim();
        StartConnectIfNeeded();

        // 启动阶段短暂等待,连接失败或超时都不阻塞启动,后续调用一律快速降级。
        try
        {
            _connectTask?.Wait(StartupConnectWait);
        }
        catch (AggregateException)
        {
            // 连接失败已在 ConnectAsync 内部处理并标记降级。
        }
    }

    /// <inheritdoc />
    public string KeyPrefix { get; }

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (_connectionString.Length == 0 || _connectionFailed)
            {
                return false;
            }

            var connection = EnsureConnection();
            return connection is { IsConnected: true };
        }
    }

    /// <inheritdoc />
    public IDatabase? GetDatabase(int db = -1)
    {
        var connection = EnsureConnection();
        if (connection is null || !connection.IsConnected)
        {
            return null;
        }

        var database = connection.GetDatabase(db);
        return string.IsNullOrWhiteSpace(KeyPrefix)
            ? database
            : database.WithKeyPrefix(KeyPrefix + ":");
    }

    /// <inheritdoc />
    public ISubscriber? GetSubscriber()
    {
        var connection = EnsureConnection();
        return connection is { IsConnected: true } ? connection.GetSubscriber() : null;
    }

    private ConnectionMultiplexer? EnsureConnection()
    {
        if (_connectionString.Length == 0 || _connectionFailed)
        {
            return null;
        }

        var connection = _connection;
        if (connection is not null)
        {
            return connection;
        }

        // 连接仍在后台建立:本次调用直接降级,绝不等待网络 IO。
        StartConnectIfNeeded();
        return null;
    }

    private void StartConnectIfNeeded()
    {
        if (_disposed
            || _connectionString.Length == 0
            || _connectionFailed
            || _connection is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed
                || _connectionFailed
                || _connection is not null
                || _connectTask is not null)
            {
                return;
            }

            _connectTask = Task.Run(ConnectAsync);
        }
    }

    private async Task ConnectAsync()
    {
        ConnectionMultiplexer? connection = null;
        try
        {
            connection = await ConnectionMultiplexer.ConnectAsync(BuildOptions()).ConfigureAwait(false);
            if (_disposed)
            {
                connection.Dispose();
                return;
            }

            _connection = connection;
        }
        catch (Exception)
        {
            // 首次连接失败:标记不可用,降级为纯 L1,不阻塞启动。
            connection?.Dispose();
            _connectionFailed = true;
        }
        finally
        {
            lock (_lock)
            {
                _connectTask = null;
            }
        }
    }

    private ConfigurationOptions BuildOptions()
    {
        var options = ConfigurationOptions.Parse(_connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ConnectTimeout = Math.Max(options.ConnectTimeout, 5000);
        options.KeepAlive = Math.Max(options.KeepAlive, 30);
        options.ReconnectRetryPolicy = new ExponentialRetry(5000);
        // 连接假死时命令最多排队 1 秒即失败,避免请求线程被长时间占用。
        options.AsyncTimeout = Math.Min(options.AsyncTimeout, CommandTimeoutMilliseconds);
        options.SyncTimeout = Math.Min(options.SyncTimeout, CommandTimeoutMilliseconds);
        return options;
    }

    public void Dispose()
    {
        _disposed = true;
        _connection?.Dispose();
        _connection = null;
    }
}
