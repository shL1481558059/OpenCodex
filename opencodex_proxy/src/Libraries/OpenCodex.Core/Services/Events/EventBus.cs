using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OpenCodex.CoreBase.Events;
using OpenCodex.Core.Services.Caching;
using StackExchange.Redis;

namespace OpenCodex.Core.Services.Events;

/// <summary>
/// 进程内 <see cref="Channel{T}"/> + Redis pub/sub 双层事件总线。
/// </summary>
/// <remarks>
/// 每个订阅者拥有独立的 <see cref="Channel{T}"/> reader,事件到达时 fan-out 写入所有匹配的订阅者。
/// 跨实例消息通过 Redis pub/sub 广播,消息信封携带类型名用于反序列化路由。
/// Redis 不可用时自动降级为纯进程内,不阻塞发布方。
/// </summary>
public sealed class EventBus : IEventBus, IDisposable
{
    private const string RedisChannelName = "eventbus";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRedisConnectionProvider? _redis;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<Guid, ISubscription> _subscriptions = new();
    private bool _redisSubscribed;
    private readonly object _subscribeLock = new();
    private bool _disposed;

    public EventBus(IRedisConnectionProvider? redis = null)
    {
        _redis = redis;
        TryEnsureRedisSubscribed();
    }

    public ChannelReader<TEvent> Subscribe<TEvent>(
        Func<TEvent, bool> filter,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<TEvent>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        var subscription = new Subscription<TEvent>(filter, channel);
        _subscriptions[id] = subscription;

        // 连接取消时自动移除订阅并完成 reader
        cancellationToken.Register(() =>
        {
            _subscriptions.Remove(id, out _);
            channel.Writer.TryComplete();
        });

        return channel.Reader;
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : notnull
    {
        var eventType = typeof(TEvent);

        // 1. 进程内 fan-out:类型匹配 + 过滤 + 写入由订阅者自管
        foreach (var sub in _subscriptions.Values)
        {
            if (sub.EventType != eventType) continue;
            sub.TryWrite(evt);
        }

        // 2. 跨实例:Redis pub/sub
        PublishToRedis(evt, eventType);
    }

    private void PublishToRedis<TEvent>(TEvent evt, Type eventType) where TEvent : notnull
    {
        if (_redis is not { IsAvailable: true }) return;

        var subscriber = _redis.GetSubscriber();
        if (subscriber is null) return;

        try
        {
            var envelope = new EventEnvelope(_instanceId, eventType.Name, JsonSerializer.Serialize(evt, JsonOptions));
            var payload = JsonSerializer.Serialize(envelope, JsonOptions);
            var channel = ResolveRedisChannel();
            subscriber.Publish(channel, payload, CommandFlags.FireAndForget);
        }
        catch
        {
            // Redis 发布失败:静默跳过,进程内订阅者已收到
        }
    }

    private void TryEnsureRedisSubscribed()
    {
        if (_redis is not { IsAvailable: true }) return;
        if (_redisSubscribed) return;

        lock (_subscribeLock)
        {
            if (_redisSubscribed) return;

            var subscriber = _redis.GetSubscriber();
            if (subscriber is null) return;

            try
            {
                subscriber.Subscribe(ResolveRedisChannel(), OnRedisMessage);
                _redisSubscribed = true;
            }
            catch
            {
                // 订阅失败:保持未订阅,后续纯进程内运行
            }
        }
    }

    private void OnRedisMessage(RedisChannel channel, RedisValue message)
    {
        if (!message.HasValue) return;

        EventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope>(message.ToString());
        }
        catch
        {
            return;
        }

        if (envelope is null) return;

        // 跳过自己发出的消息
        if (string.Equals(envelope.SenderId, _instanceId, StringComparison.Ordinal)) return;

        // 按事件类型名路由到对应订阅者
        DispatchRedisEvent(envelope);
    }

    private void DispatchRedisEvent(EventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case nameof(ChannelCapacityChangedEvent):
                var capacityEvt = TryDeserialize<ChannelCapacityChangedEvent>(envelope.Payload);
                if (capacityEvt is not null) FanOut(capacityEvt);
                break;
            case nameof(RequestLogWrittenEvent):
                var logEvt = TryDeserialize<RequestLogWrittenEvent>(envelope.Payload);
                if (logEvt is not null) FanOut(logEvt);
                break;
        }
    }

    private void FanOut<TEvent>(TEvent evt) where TEvent : notnull
    {
        var eventType = typeof(TEvent);
        foreach (var sub in _subscriptions.Values)
        {
            if (sub.EventType != eventType) continue;
            sub.TryWrite(evt);
        }
    }

    private static TEvent? TryDeserialize<TEvent>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TEvent>(json, JsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private RedisChannel ResolveRedisChannel()
    {
        var name = string.IsNullOrWhiteSpace(_redis?.KeyPrefix)
            ? RedisChannelName
            : $"{_redis.KeyPrefix}:{RedisChannelName}";
        return RedisChannel.Literal(name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions.Values)
        {
            sub.TryComplete();
        }
        _subscriptions.Clear();
    }

    private sealed record EventEnvelope(string SenderId, string EventType, string Payload);

    private interface ISubscription
    {
        Type EventType { get; }
        bool TryWrite(object evt);
        void TryComplete();
    }

    private sealed class Subscription<TEvent>(
        Func<TEvent, bool> filter,
        Channel<TEvent> channel) : ISubscription
    {
        public Type EventType => typeof(TEvent);

        public bool TryWrite(object evt)
        {
            if (evt is not TEvent typed) return false;
            if (!filter(typed)) return false;
            return channel.Writer.TryWrite(typed);
        }

        public void TryComplete() => channel.Writer.TryComplete();
    }
}
