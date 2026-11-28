using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using Xunit.Abstractions;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

[CollectionDefinition("WS long history regression", DisableParallelization = true)]
public sealed class WsLongHistoryRegressionCollection;

[Collection("WS long history regression")]
public sealed class WsLongHistoryRegressionTests(ITestOutputHelper log)
{
    private const string FixtureModel = "fixture-model";
    private static readonly TestParameters Settings = TestParameters.Load();

    [Fact]
    public Task SmallFirstRequestThenSmallDeltaCompletesOnSameConnection() =>
        RunConversationAsync(Settings.SmallRequestBytes, fullResend: false);

    [Fact]
    public Task HistoricalSizedFirstRequestThenSmallDeltaCompletesOnSameConnection() =>
        RunConversationAsync(Settings.HistoricalRequestBytes, fullResend: false);

    [Fact]
    public Task SmallFirstRequestThenHistoricalSizedFullResendCompletesOnSameConnection() =>
        RunConversationAsync(Settings.SmallRequestBytes, fullResend: true);

    [Fact]
    public async Task FakeSocketReassemblesFragmentsAndRecordsActualCloseArguments()
    {
        using var timeout = new CancellationTokenSource(Settings.CaseTimeoutMilliseconds);
        using var socket = new FragmentedWebSocket(Settings.ReceiveFragmentBytes);
        var request = SizedRequest(Settings.SmallRequestBytes, 'x');
        var requestBytes = socket.ClientSend(request);
        var buffer = new byte[Math.Min(Settings.ReceiveFragmentBytes, requestBytes / 2)];
        using var received = new MemoryStream();
        ValueWebSocketReceiveResult fragment;
        do
        {
            fragment = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, fragment.MessageType);
            received.Write(buffer, 0, fragment.Count);
        }
        while (!fragment.EndOfMessage);
        Assert.Equal(requestBytes, received.Length);
        Assert.True(socket.ReceiveFragmentCount > 1);
        var reconstructed = Parse(Encoding.UTF8.GetString(received.ToArray()));
        AssertContent(request, reconstructed);

        var wire = JsonSerializer.SerializeToUtf8Bytes(Terminal(Message("MOCK")));
        var split = wire.Length / 2;
        await socket.SendAsync(new ArraySegment<byte>(wire, 0, split),
            WebSocketMessageType.Text, endOfMessage: false, timeout.Token);
        Assert.False(socket.Outgoing.Reader.TryRead(out _));
        await socket.SendAsync(wire.AsMemory(split),
            WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
        var completed = await CompletedAsync(socket, timeout.Token);
        Assert.Equal("completed", JsonDictionaryValue.String(completed, "status"));
        Assert.Equal(2, socket.SendFragmentCount);
        Assert.Equal(1, socket.SendMessageCount);

        socket.ClientClose(WebSocketCloseStatus.NormalClosure, "fixture client close");
        var close = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.True(close.EndOfMessage);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
        Assert.Equal("fixture client close", close.CloseStatusDescription);
        Assert.Equal(close.CloseStatus, socket.CloseStatus);
        Assert.Equal(close.CloseStatusDescription, socket.CloseStatusDescription);

        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "fixture output close", timeout.Token);
        await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "fixture full close", timeout.Token);
        Assert.Collection(socket.ServerCloseCalls,
            call =>
            {
                Assert.Equal(nameof(WebSocket.CloseOutputAsync), call.Method);
                Assert.Equal(WebSocketCloseStatus.PolicyViolation, call.Status);
                Assert.Equal("fixture output close", call.Description);
            },
            call =>
            {
                Assert.Equal(nameof(WebSocket.CloseAsync), call.Method);
                Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, call.Status);
                Assert.Equal("fixture full close", call.Description);
            });
        // CloseStatus describes the received peer frame; requested server statuses
        // are independently recorded rather than invented from the socket state.
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private async Task RunConversationAsync(int firstRequestBytes, bool fullResend)
    {
        using var context = new MultiAgentApiTestContext(
            ctx => Stream(ctx, Events(Terminal(Message("MOCK")))));
        context.CatalogEnabled = false;
        context.Lifetime.CancelAfter(Settings.CaseTimeoutMilliseconds);
        using var socket = new FragmentedWebSocket(Settings.ReceiveFragmentBytes);
        context.Http.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
        var first = SizedRequest(firstRequestBytes, 'h');
        var secondBytes = fullResend ? Settings.HistoricalRequestBytes : Settings.SmallRequestBytes;
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            Assert.Equal(firstRequestBytes, socket.ClientSend(first));
            var firstResponse = await CompletedAsync(socket, context.Lifetime.Token);
            var firstId = JsonDictionaryValue.String(firstResponse, "id");
            Assert.False(string.IsNullOrWhiteSpace(firstId));
            Assert.Single(context.FakeEndpoint.Calls);
            AssertContent(first, Assert.IsType<D>(Assert.Single(
                JsonDictionaryValue.List(context.FakeEndpoint.Calls[0].Payload!, "input"))));

            var second = SizedRequest(secondBytes, 'd', fullResend ? null : firstId);
            Assert.Equal(secondBytes, socket.ClientSend(second));
            var secondResponse = await CompletedAsync(socket, context.Lifetime.Token);
            Assert.NotEqual(firstId, JsonDictionaryValue.String(secondResponse, "id"));
            Assert.Equal(2, context.FakeEndpoint.Calls.Count);
            foreach (var call in context.FakeEndpoint.Calls)
            {
                Assert.Equal(FixtureModel, JsonDictionaryValue.String(call.Payload!, "model"));
                Assert.False(Assert.IsType<bool>(JsonDictionaryValue.Get(call.Payload!, "store")));
                Assert.False(call.Payload!.ContainsKey("previous_response_id"));
            }

            var projected = JsonDictionaryValue.List(context.FakeEndpoint.Calls[1].Payload!, "input");
            if (fullResend)
            {
                AssertContent(second, Assert.IsType<D>(Assert.Single(projected)));
            }
            else
            {
                Assert.Equal(3, projected.Count);
                AssertContent(first, Assert.IsType<D>(projected[0]));
                var assistant = Assert.IsType<D>(projected[1]);
                Assert.Equal("assistant", JsonDictionaryValue.String(assistant, "role"));
                var outputText = Assert.IsType<D>(Assert.Single(JsonDictionaryValue.List(assistant, "content")));
                Assert.Equal("MOCK", JsonDictionaryValue.String(outputText, "text"));
                AssertContent(second, Assert.IsType<D>(projected[2]));
                Assert.Equal(firstId, JsonDictionaryValue.String(
                    Assert.IsType<D>(context.FakeEndpoint.Calls[1].ClientPayload), "previous_response_id"));
            }
            Assert.Equal(new[] { firstRequestBytes, secondBytes }, socket.ReceivedMessageBytes);
            Assert.Equal((long)firstRequestBytes + secondBytes, socket.ReceivedBytes);
            Assert.Equal(2, socket.SendMessageCount);
        }
        finally
        {
            socket.ClientClose(WebSocketCloseStatus.NormalClosure, "Regression finished");
            try
            {
                await operation.WaitAsync(context.Lifetime.Token);
            }
            finally
            {
                log.WriteLine($"firstBytes={firstRequestBytes}; secondBytes={secondBytes}; fullResend={fullResend}; " +
                    $"receivedBytes={socket.ReceivedBytes}; receiveFragments={socket.ReceiveFragmentCount}; " +
                    $"sentFragments={socket.SendFragmentCount}; modelCalls={context.FakeEndpoint.Calls.Count}; " +
                    $"state={socket.State}; close={socket.CloseTrace}");
            }
        }
        Assert.Contains(socket.ServerCloseCalls, call => call.Status == WebSocketCloseStatus.NormalClosure);
        Assert.DoesNotContain(socket.ServerCloseCalls, call => call.Status != WebSocketCloseStatus.NormalClosure);
    }

    private async Task<D> CompletedAsync(FragmentedWebSocket socket, CancellationToken ct)
    {
        while (await socket.Outgoing.Reader.WaitToReadAsync(ct))
        {
            var item = await socket.Outgoing.Reader.ReadAsync(ct);
            var type = Type(item);
            if (type is "error" or "response.failed" or "response.incomplete")
            {
                // Fail on the first error/unsuccessful terminal. Waiting exclusively
                // for completed would hide the red regression behind a timeout.
                Assert.Fail($"Expected response.completed; received {JsonSerializer.Serialize(item)}; " +
                    $"receivedBytes={socket.ReceivedBytes}; close={socket.CloseTrace}");
            }
            if (type != "response.completed") continue;
            var response = Assert.IsType<D>(item["response"]);
            Assert.Equal("completed", JsonDictionaryValue.String(response, "status"));
            Assert.False(string.IsNullOrWhiteSpace(JsonDictionaryValue.String(response, "id")));
            return response;
        }
        Assert.Fail($"WebSocket ended before response.completed; state={socket.State}; close={socket.CloseTrace}");
        return null!;
    }

    private static D SizedRequest(int bytes, char textCharacter, string? previousId = null)
    {
        var request = new D
        {
            ["type"] = "response.create", ["model"] = FixtureModel, ["store"] = false, ["input"] = ""
        };
        if (previousId is not null) request["previous_response_id"] = previousId;
        var overhead = JsonSerializer.SerializeToUtf8Bytes(request).Length;
        if (bytes <= overhead) throw new ArgumentOutOfRangeException(nameof(bytes), "Request budget must exceed the JSON wrapper.");
        // ASCII needs neither UTF-8 expansion nor JSON escaping, so the entire
        // encoded request, including previous_response_id, has the exact size.
        request["input"] = new string(textCharacter, bytes - overhead);
        return request;
    }

    private static void AssertContent(D request, D projected)
    {
        var expected = Assert.IsType<string>(request["input"]);
        var actual = JsonDictionaryValue.String(projected, projected.ContainsKey("content") ? "content" : "input");
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "The complete request text must reach the model unchanged.");
    }

    private sealed record TestParameters(int HistoricalRequestBytes, int SmallRequestBytes,
        int ReceiveFragmentBytes, int CaseTimeoutMilliseconds)
    {
        public static TestParameters Load()
        {
            var result = new TestParameters(WsHistoryTestParameters.HistoricalRequestBytes,
                WsHistoryTestParameters.SmallRequestBytes,
                WsHistoryTestParameters.ReceiveFragmentBytes,
                WsHistoryTestParameters.CaseTimeoutMilliseconds);
            if (result.HistoricalRequestBytes <= result.SmallRequestBytes || result.HistoricalRequestBytes <= result.ReceiveFragmentBytes)
                throw new InvalidOperationException("HistoricalRequestBytes must exceed the small request and receive fragment sizes.");
            return result;
        }
    }

    private sealed class SocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class FragmentedWebSocket : WebSocket
    {
        private sealed record ClientMessage(byte[]? Bytes, WebSocketCloseStatus? CloseStatus = null, string? CloseDescription = null);
        public sealed record CloseCall(string Method, WebSocketCloseStatus Status, string? Description);

        private readonly int _fragmentBytes;
        private readonly Channel<ClientMessage> _incoming = Channel.CreateUnbounded<ClientMessage>();
        public Channel<D> Outgoing { get; } = Channel.CreateUnbounded<D>();
        private readonly MemoryStream _pendingSend = new();
        private readonly object _recordGate = new();
        private readonly List<int> _receivedMessageBytes = [];
        private readonly List<CloseCall> _closeCalls = [];
        private byte[]? _receiving;
        private int _receiveOffset;
        private WebSocketMessageType? _sendingType;
        private volatile WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _peerCloseStatus;
        private string? _peerCloseDescription;
        private WebSocketCloseStatus? _localCloseStatus;
        private string? _localCloseDescription;
        private long _receivedBytes;
        private int _receiveFragmentCount;
        private int _sendFragmentCount;
        private int _sendMessageCount;
        private bool _disposed;

        public FragmentedWebSocket(int fragmentBytes)
        {
            if (fragmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(fragmentBytes));
            _fragmentBytes = fragmentBytes;
        }

        public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
        public int ReceiveFragmentCount => Volatile.Read(ref _receiveFragmentCount);
        public int SendFragmentCount => Volatile.Read(ref _sendFragmentCount);
        public int SendMessageCount => Volatile.Read(ref _sendMessageCount);
        public int[] ReceivedMessageBytes { get { lock (_recordGate) return _receivedMessageBytes.ToArray(); } }
        public CloseCall[] ServerCloseCalls { get { lock (_recordGate) return _closeCalls.ToArray(); } }
        public string CloseTrace => string.Join("; ", ServerCloseCalls.Select(call => $"{call.Method}({call.Status}, {call.Description})"));
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => _peerCloseStatus ?? _localCloseStatus;
        public override string? CloseStatusDescription => _peerCloseStatus.HasValue ? _peerCloseDescription : _localCloseDescription;
        public override string? SubProtocol => null;

        public int ClientSend(D request)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            if (_state != WebSocketState.Open || !_incoming.Writer.TryWrite(new ClientMessage(bytes)))
                throw new InvalidOperationException("Cannot queue a request on a closed fixture socket.");
            return bytes.Length;
        }

        public void ClientClose(WebSocketCloseStatus status, string? description)
        {
            if (_state == WebSocketState.Open) _incoming.Writer.TryWrite(new ClientMessage(null, status, description));
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) =>
            ReceiveCoreAsync(buffer.AsMemory(), ct).AsTask();

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var result = await ReceiveCoreAsync(buffer, ct);
            return new ValueWebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
        }

        private async ValueTask<WebSocketReceiveResult> ReceiveCoreAsync(Memory<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length == 0) throw new ArgumentException("Receive buffer must not be empty.", nameof(buffer));
            if (_receiving is null)
            {
                var message = await _incoming.Reader.ReadAsync(ct);
                _receiveOffset = 0;
                if (message.Bytes is null)
                {
                    _peerCloseStatus = message.CloseStatus;
                    _peerCloseDescription = message.CloseDescription;
                    _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, message.CloseStatus, message.CloseDescription);
                }
                _receiving = message.Bytes;
            }
            var bytes = _receiving!;
            var count = Math.Min(Math.Min(_fragmentBytes, buffer.Length), bytes.Length - _receiveOffset);
            bytes.AsMemory(_receiveOffset, count).CopyTo(buffer);
            _receiveOffset += count;
            Interlocked.Add(ref _receivedBytes, count);
            Interlocked.Increment(ref _receiveFragmentCount);
            var end = _receiveOffset == bytes.Length;
            if (end)
            {
                lock (_recordGate) _receivedMessageBytes.Add(bytes.Length);
                _receiving = null;
            }
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken ct)
        {
            SendCore(buffer.AsMemory(), messageType, endOfMessage, ct);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken ct = default)
        {
            SendCore(buffer, messageType, endOfMessage, ct);
            return ValueTask.CompletedTask;
        }

        private void SendCore(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_state != WebSocketState.Open) throw new WebSocketException(WebSocketError.InvalidState);
            if (messageType != WebSocketMessageType.Text) throw new InvalidOperationException("The fixture expects text JSON server messages.");
            if (_sendingType.HasValue && _sendingType.Value != messageType)
                throw new InvalidOperationException("Fragmented messages must keep their message type.");
            _sendingType = messageType;
            _pendingSend.Write(buffer.Span);
            Interlocked.Increment(ref _sendFragmentCount);
            if (!endOfMessage) return;
            var item = Parse(Encoding.UTF8.GetString(_pendingSend.GetBuffer(), 0, checked((int)_pendingSend.Length)));
            _pendingSend.SetLength(0);
            _sendingType = null;
            Interlocked.Increment(ref _sendMessageCount);
            if (!Outgoing.Writer.TryWrite(item)) throw new InvalidOperationException("Cannot send on a closed fixture socket.");
        }

        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            RecordClose(nameof(CloseAsync), status, description, ct);
            _state = WebSocketState.Closed;
            Outgoing.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            RecordClose(nameof(CloseOutputAsync), status, description, ct);
            _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
            Outgoing.Writer.TryComplete();
            return Task.CompletedTask;
        }

        private void RecordClose(string method, WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _localCloseStatus = status;
            _localCloseDescription = description;
            lock (_recordGate) _closeCalls.Add(new CloseCall(method, status, description));
        }

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
            Outgoing.Writer.TryComplete();
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_state != WebSocketState.Aborted) _state = WebSocketState.Closed;
            _receiving = null;
            _incoming.Writer.TryComplete();
            Outgoing.Writer.TryComplete();
            _pendingSend.Dispose();
        }
    }
}
