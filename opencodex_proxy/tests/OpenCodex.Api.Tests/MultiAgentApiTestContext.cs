using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCodex.Api.Services;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

internal sealed class MultiAgentApiTestContext : IDisposable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MultiAgentRunStore, NativeClientSessionStore> NativeStores = new();
    private readonly ServiceProvider _services;
    public DefaultHttpContext Http { get; } = new();
    public RecordingStream Body { get; } = new();
    public MultiAgentResponseService Service { get; }
    public Endpoint FakeEndpoint { get; }
    public MultiAgentRunStore Store { get; }
    public MultiAgentClientStore ClientStore { get; }
    public NativeClientSessionStore NativeStore { get; }
    public Guid Key { get; }
    public bool CatalogEnabled { get; set; } = true;
    public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(10));

    public MultiAgentApiTestContext(Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler,
        MultiAgentRunStore? store = null, Guid? key = null, string session = "test-session",
        MultiAgentClientStore? clientStore = null)
    {
        Store = store ?? new MultiAgentRunStore(Path.Combine(Path.GetTempPath(), "ocxp-api-tests-" + Guid.NewGuid().ToString("N")));
        ClientStore = clientStore ?? new MultiAgentClientStore(Store,
            Path.Combine(Path.GetTempPath(), "ocxp-api-client-tests-" + Guid.NewGuid().ToString("N")));
        NativeStore = NativeStores.GetValue(Store, _ => new NativeClientSessionStore(
            Path.Combine(Path.GetTempPath(), "ocxp-api-native-tests-" + Guid.NewGuid().ToString("N"))));
        Key = key ?? Guid.NewGuid();
        Http.Request.Path = "/v1/responses";
        Http.Request.Method = "POST";
        Http.Request.Headers["session-id"] = session;
        Http.Response.Body = Body;
        Http.RequestAborted = Lifetime.Token;
        FakeEndpoint = new Endpoint(handler);
        _services = new ServiceCollection().AddSingleton(NativeStore).AddScoped<IProxyEndpointService>(_ => FakeEndpoint).BuildServiceProvider();
        Http.RequestServices = _services;
        var catalog = DispatchProxy.Create<IModelCatalogService, CatalogProxy>();
        ((CatalogProxy)(object)catalog).Enabled = () => CatalogEnabled;
        Service = new MultiAgentResponseService(new FixedContextAccessor { HttpContext = Http }, catalog,
            new Identity(Key), FakeEndpoint, Store, _services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<MultiAgentResponseService>.Instance, NativeStore);
    }

    public TestSocket Socket()
    {
        var socket = new TestSocket();
        Http.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
        return socket;
    }
    public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); _services.Dispose(); Body.Dispose(); }
    public static D Request(bool stream = false) => new()
    {
        ["model"] = "fake", ["store"] = false, ["stream"] = stream,
        ["input"] = new List<object?> { new D { ["role"] = "user", ["content"] = "work" } }
    };
    public static D Message(string text) => new()
    {
        ["type"] = "message", ["id"] = "up_item", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new List<object?> { new D { ["type"] = "output_text", ["text"] = text } }
    };
    public static D Terminal(params D[] items) => new()
    {
        ["type"] = "response.completed", ["response"] = new D
        {
            ["id"] = "resp_" + Guid.NewGuid().ToString("N"), ["status"] = "completed", ["output"] = items.Cast<object?>().ToList(),
            ["usage"] = new D { ["input_tokens"] = 1, ["output_tokens"] = 1 }
        }
    };
    public static async Task<ProxyEndpointResult> Stream(ProxyEndpointContext context, IAsyncEnumerable<D> events)
    {
        context.StreamWriter.PrepareSse();
        await context.StreamWriter.WriteLinesAsync(Lines(events, context.CancellationToken), _ => true, () => 1, context.CancellationToken);
        return new ProxyEndpointResult(200, null, true);
    }
    public static async IAsyncEnumerable<D> Events(params D[] items)
    {
        foreach (var item in items) { yield return item; await Task.Yield(); }
    }
    private static async IAsyncEnumerable<string> Lines(IAsyncEnumerable<D> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in events.WithCancellation(ct)) yield return "data: " + JsonSerializer.Serialize(item) + "\n\n";
    }
    public static D Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (D)WebSearchPayload.FromJsonElement(doc.RootElement)!;
    }
    public static string Type(D item) => JsonDictionaryValue.String(item, "type");

    internal sealed class Endpoint(Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler) : IProxyEndpointService
    {
        public List<ProxyEndpointContext> Calls { get; } = [];
        public Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context)
        {
            lock (Calls) Calls.Add(context);
            return handler(context);
        }
    }
    private sealed class FixedContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
    private sealed class Identity(Guid key) : IProxyIdentityContext
    {
        public bool IsAuthenticated => true;
        public ProxyIdentity Current { get; } = new(key, Guid.NewGuid(), "test", "user");
        public ProxyIdentity RequireIdentity() => Current;
    }
    public class CatalogProxy : DispatchProxy
    {
        public Func<bool> Enabled { get; set; } = () => true;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "SimulatesMultiAgent" ? Enabled() : throw new NotSupportedException(method?.Name);
    }
    private sealed class SocketFeature(TestSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult<WebSocket>(socket);
    }
    internal sealed class RecordingStream : MemoryStream
    {
        public Channel<string> Writes { get; } = Channel.CreateUnbounded<string>();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Writes.Writer.TryWrite(Encoding.UTF8.GetString(buffer.Span));
            return base.WriteAsync(buffer, ct);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Writes.Writer.TryWrite(Encoding.UTF8.GetString(buffer, offset, count));
            return base.WriteAsync(buffer, offset, count, ct);
        }
        public string Text => Encoding.UTF8.GetString(ToArray());
    }
    internal sealed class TestSocket : WebSocket
    {
        private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
        public Channel<D> Outgoing { get; } = Channel.CreateUnbounded<D>();
        public Channel<D> Received { get; } = Channel.CreateUnbounded<D>();
        public Func<D, CancellationToken, Task>? OnServerSend { get; set; }
        public List<D> Sent { get; } = [];
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Open ? null : WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public void ClientSend(D item) => _incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(item));
        public void ClientRaw(string value) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(value));
        public void ClientClose() => _incoming.Writer.TryWrite(null);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var bytes = await _incoming.Reader.ReadAsync(ct);
            if (bytes is null) { _state = WebSocketState.CloseReceived; return new(0, WebSocketMessageType.Close, true); }
            bytes.CopyTo(buffer.AsSpan());
            try { Received.Writer.TryWrite(Parse(Encoding.UTF8.GetString(bytes))); } catch (JsonException) { }
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var item = Parse(Encoding.UTF8.GetString(buffer));
            Sent.Add(item);
            Outgoing.Writer.TryWrite(item);
            if (OnServerSend is not null) await OnServerSend(item, ct);
        }
        public async Task<D> Until(string type, CancellationToken ct)
        {
            while (true) { var item = await Outgoing.Reader.ReadAsync(ct); if (Type(item) == type) return item; }
        }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseOutputAsync(status, description, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
