using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Config;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>Maintains isolated Responses WebSocket connections for context reuse.</summary>
public sealed class ResponsesWebSocketPool : IAsyncDisposable
{
    private const int MaximumConnections = 64;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<ResponsesWebSocketLease> AcquireAsync(
        string key,
        IReadOnlyDictionary<string, object?> channel,
        int defaultTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ConnectionEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CleanupExpired();
            if (!_connections.TryGetValue(key, out entry!))
            {
                if (_connections.Count >= MaximumConnections && !EvictOldest())
                {
                    throw new UpstreamException(
                        "upstream Responses WebSocket pool is full",
                        ProxyHttpStatus.ServiceUnavailable);
                }
                entry = new ConnectionEntry();
                _connections[key] = entry;
            }
            entry.Reserve();
        }

        var gateHeld = false;
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
            gateHeld = true;
            entry.AcquisitionCompleted();
            if (entry.Socket is null || entry.Socket.State is not (WebSocketState.Open or WebSocketState.Connecting))
            {
                entry.Socket?.Dispose();
                entry.Socket = await ConnectAsync(channel, defaultTimeoutSeconds, cancellationToken);
            }
            entry.LastUsed = DateTimeOffset.UtcNow;
            return new ResponsesWebSocketLease(entry);
        }
        catch
        {
            if (gateHeld)
            {
                entry.Socket?.Dispose();
                entry.Socket = null;
                entry.Gate.Release();
                entry.AcquisitionFailed();
            }
            else
            {
                entry.CancelWait();
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        List<ConnectionEntry> entries;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            entries = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var entry in entries)
        {
            entry.Socket?.Dispose();
            entry.Gate.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        IReadOnlyDictionary<string, object?> channel,
        int defaultTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ApplyHeaders(socket, channel);
        var timeout = TimeoutSeconds(channel, defaultTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            await socket.ConnectAsync(ResponsesUrl(channel), timeoutCts.Token);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new UpstreamException(
                "upstream WebSocket connection timed out",
                ProxyHttpStatus.GatewayTimeout,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }
        catch (WebSocketException exception)
        {
            socket.Dispose();
            throw new UpstreamException(
                $"failed to connect upstream WebSocket: {exception.Message}",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }
    }

    private static void ApplyHeaders(ClientWebSocket socket, IReadOnlyDictionary<string, object?> channel)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ConfigValue.TryAsObject(JsonDictionaryValue.Get(channel, "headers"), out var configured))
        {
            foreach (var pair in configured)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    headers[pair.Key] = pair.Value.ToString() ?? string.Empty;
            }
        }

        var authMode = JsonDictionaryValue.String(channel, "auth_mode");
        var apiKey = JsonDictionaryValue.String(channel, "apikey");
        if ((authMode.Length == 0 || authMode == "config") && apiKey.Length > 0
            && !headers.ContainsKey("authorization"))
        {
            headers["authorization"] = "Bearer " + apiKey;
        }
        if (!headers.ContainsKey("user-agent"))
            headers["user-agent"] = "OpenCodex-Proxy/0.1";

        foreach (var (name, value) in headers)
        {
            socket.Options.SetRequestHeader(name, value);
        }
    }

    private static Uri ResponsesUrl(IReadOnlyDictionary<string, object?> channel)
    {
        var baseUrl = JsonDictionaryValue.String(channel, "baseurl").TrimEnd('/');
        var endpoint = baseUrl.EndsWith("/v1", StringComparison.Ordinal)
            ? baseUrl + "/responses"
            : baseUrl + "/v1/responses";
        var builder = new UriBuilder(endpoint)
        {
            Scheme = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
        };
        return builder.Uri;
    }

    private static int TimeoutSeconds(IReadOnlyDictionary<string, object?> channel, int defaultTimeout)
    {
        var value = JsonDictionaryValue.Get(channel, "timeout_seconds");
        return value switch
        {
            int number when number > 0 => number,
            long number when number > 0 && number <= int.MaxValue => (int)number,
            double number when number > 0 && number <= int.MaxValue => (int)number,
            string text when int.TryParse(text, out var parsed) && parsed > 0 => parsed,
            _ => defaultTimeout
        };
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleLifetime;
        foreach (var (key, entry) in _connections.ToList())
        {
            if (entry.IsReserved) continue;
            if (entry.LastUsed >= cutoff && entry.Socket?.State is WebSocketState.Open or WebSocketState.Connecting)
                continue;
            _connections.Remove(key);
            entry.Socket?.Dispose();
            entry.Gate.Dispose();
        }
    }

    private bool EvictOldest()
    {
        var oldest = _connections
            .Where(pair => !pair.Value.IsReserved)
            .MinBy(pair => pair.Value.LastUsed);
        if (oldest.Key is null) return false;
        _connections.Remove(oldest.Key);
        oldest.Value.Socket?.Dispose();
        oldest.Value.Gate.Dispose();
        return true;
    }

    internal sealed class ConnectionEntry
    {
        private int _pending;
        private int _active;

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ClientWebSocket? Socket { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public bool IsReserved => Volatile.Read(ref _pending) > 0 || Volatile.Read(ref _active) > 0;
        public void Reserve() => Interlocked.Increment(ref _pending);
        public void CancelWait() => Interlocked.Decrement(ref _pending);
        public void AcquisitionCompleted()
        {
            Interlocked.Increment(ref _active);
            Interlocked.Decrement(ref _pending);
        }
        public void AcquisitionFailed() => Interlocked.Decrement(ref _active);
        public void LeaseReleased() => Interlocked.Decrement(ref _active);
    }
}

/// <summary>Exclusive lease for one request on a pooled Responses WebSocket.</summary>
public sealed class ResponsesWebSocketLease : IAsyncDisposable
{
    private readonly ResponsesWebSocketPool.ConnectionEntry _entry;
    private bool _disposed;

    internal ResponsesWebSocketLease(ResponsesWebSocketPool.ConnectionEntry entry)
    {
        _entry = entry;
    }

    private ClientWebSocket Socket => _entry.Socket
        ?? throw new InvalidOperationException("The upstream WebSocket is unavailable.");

    public async Task SendAsync(
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var message = WebSearchPayload.DeepCopyObject(payload);
        message["type"] = "response.create";
        message.Remove("stream");
        message.Remove("stream_options");
        message.Remove("background");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task<Dictionary<string, object?>?> ReceiveAsync(
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult received;
        do
        {
            try
            {
                received = await Socket.ReceiveAsync(buffer, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpstreamException("upstream WebSocket stream idle timeout", ProxyHttpStatus.GatewayTimeout);
            }
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 32 * 1024 * 1024)
                throw new UpstreamException("upstream WebSocket returned an invalid message.", ProxyHttpStatus.BadGateway);
            message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        try
        {
            using var json = JsonDocument.Parse(message.ToArray());
            return WebSearchPayload.FromJsonElement(json.RootElement) as Dictionary<string, object?>
                ?? throw new JsonException("A JSON object is required.");
        }
        catch (JsonException)
        {
            throw new UpstreamException("upstream WebSocket returned invalid JSON.", ProxyHttpStatus.BadGateway);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _entry.LastUsed = DateTimeOffset.UtcNow;
        _entry.Gate.Release();
        _entry.LeaseReleased();
        await ValueTask.CompletedTask;
    }

    public void Abort()
    {
        if (_disposed) return;
        _entry.Socket?.Dispose();
        _entry.Socket = null;
    }

    public static string Fingerprint(IReadOnlyDictionary<string, object?> channel)
    {
        var source = string.Join('\n',
            JsonDictionaryValue.String(channel, "id"),
            JsonDictionaryValue.String(channel, "baseurl"),
            JsonDictionaryValue.String(channel, "auth_mode"),
            JsonDictionaryValue.String(channel, "apikey"),
            JsonDictionaryValue.Get(channel, "headers") is object headers
                ? JsonValueCanonicalizer.Canonicalize(headers)
                : "{}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
