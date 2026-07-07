using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;

namespace OpenCodex.Core.Services.Caching;

/// <summary>
/// 基于 <see cref="ConnectionMultiplexer"/> 的 Redis 连接提供者。
/// </summary>
/// <remarks>
/// 懒加载 + 双检锁单例,参考 shl_file_server 的连接配置:
/// <c>AbortOnConnectFail=false</c>、<c>ConnectRetry=3</c>、<c>ExponentialRetry(5000)</c>。
/// 连接串为空时直接禁用(<see cref="IsAvailable"/>=false);首次连接失败时标记不可用并降级,
/// 不抛异常、不阻塞服务启动。key 前缀通过 <c>WithKeyPrefix</c> 统一挂载。
/// </remarks>
public sealed class RedisConnectionProvider : IRedisConnectionProvider, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();
    private volatile ConnectionMultiplexer? _connection;
    private volatile bool _connectionFailed;

    public RedisConnectionProvider(string? connectionString, string? keyPrefix)
    {
        _connectionString = (connectionString ?? string.Empty).Trim();
        KeyPrefix = string.IsNullOrWhiteSpace(keyPrefix) ? "opencodex" : keyPrefix.Trim();
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
        if (connection is null)
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
        return connection?.GetSubscriber();
    }

    private ConnectionMultiplexer? EnsureConnection()
    {
        if (_connectionString.Length == 0 || _connectionFailed)
        {
            return null;
        }

        if (_connection != null)
        {
            return _connection;
        }

        lock (_lock)
        {
            if (_connection != null)
            {
                return _connection;
            }

            if (_connectionFailed)
            {
                return null;
            }

            try
            {
                _connection = ConnectionMultiplexer.Connect(BuildOptions());
            }
            catch (Exception)
            {
                // 首次连接失败:标记不可用,降级为纯 L1,不阻塞启动。
                _connectionFailed = true;
                return null;
            }

            return _connection;
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
        return options;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
