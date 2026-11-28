using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

/// <summary>
/// A genuine bounded source failure followed by successful audit persistence. No model/endpoint
/// throw manufactures the cap. Only the optional delivery-fault row injects a second failure,
/// after that cap has already occurred. The WS row uses the real endpoint, stream and model writer.
/// </summary>
public sealed class CoreWsFailureContractRegressionTests
{
    private static readonly Limits Settings = Limits.Read();
    private const string ResponseId = "resp_core_local_capture_failure";
    private const string CaptureCode = "evidence_capture_limit";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreCaptureFailureSurvivesModelWriterErrorDelivery(bool rejectErrorCallback)
    {
        using var fixture = new Fixture();
        fixture.Source.Release.TrySetResult();
        var seen = new List<D>();
        var writer = new MultiAgentModelStreamWriter((payload, _) =>
        {
            seen.Add(WebSearchPayload.DeepCopyObject(payload));
            // This is a separate failed terminal-delivery probe, not the source of the local cap.
            if (rejectErrorCallback && Type(payload) == "error")
                throw new UpstreamException("Injected failure delivering an already-failed terminal.");
            return Task.CompletedTask;
        }, fixture.Frames);

        var failure = await Record.ExceptionAsync(() => fixture.Streams.StreamAsync(
            fixture.StreamContext(writer, fixture.Deadline.Token)));

        var local = Assert.IsType<StorageCapacityException>(failure);
        Assert.Equal(507, local.StatusCode);
        Assert.Equal(CaptureCode, local.ErrorCode);
        Assert.Contains(seen, item => Type(item) == "response.created");
        Assert.Contains(seen, item => Type(item) == "response.output_text.delta");
        Assert.Equal(CaptureCode, ErrorCode(Assert.Single(seen.Where(IsFailureTerminal))));
        Assert.DoesNotContain(seen, item => Type(item) == "response.completed");
        Assert.Equal(1, fixture.Source.Calls);
        Assert.True(fixture.Source.OfferedFrames >= 3);
        AssertCapturedFailure(Assert.Single(fixture.Logs.Rows));
        fixture.Logs.Dispose();
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public async Task OrdinaryWsLocalFailureIsSingleTerminalAndDoesNotPoisonCircuitOrEarlierHistory()
    {
        using var fixture = new Fixture();
        var endpointFinished = NewSignal();
        using var context = fixture.Context(async endpoint =>
        {
            try { return await fixture.Endpoint.ProxyAsync(endpoint); }
            finally { endpointFinished.TrySetResult(); }
        });
        context.CatalogEnabled = false;
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        D? firstFailure = null;
        D? missingHistory = null;
        D? retainedHistory = null;
        try
        {
            socket.ClientSend(Create(generate: false));
            var warm = Response(await socket.Until("response.completed", context.Lifetime.Token));
            var warmId = JsonDictionaryValue.String(warm, "id");
            socket.ClientSend(Create(warmId));
            await socket.Until("response.created", context.Lifetime.Token);
            await socket.Until("response.output_text.delta", context.Lifetime.Token);

            // The source is paused after real output. This recoverable request error must NOT
            // acquire the model response's terminal ownership when the API error gate is fixed.
            socket.ClientSend(Create());
            var inProgress = await ReadUntil(socket,
                item => ErrorCode(item) == "response_in_progress", context.Lifetime.Token);
            Assert.Equal(400, WireStatus(inProgress));
            fixture.Source.Release.TrySetResult();
            firstFailure = await ReadUntil(socket, IsFailureTerminal, context.Lifetime.Token);
            await endpointFinished.Task.WaitAsync(context.Lifetime.Token);

            // A protocol round-trip, not reflection/polling: processing this next create waits
            // for the previous request to unwind, including any incorrectly duplicated terminal.
            socket.ClientSend(Create(ResponseId));
            missingHistory = await ReadUntil(socket,
                item => ErrorCode(item) == "previous_response_not_found", context.Lifetime.Token);
            socket.ClientSend(Create(warmId, generate: false));
            retainedHistory = await socket.Until("response.completed", context.Lifetime.Token);
        }
        finally
        {
            fixture.Source.Release.TrySetResult();
            socket.ClientClose();
            await operation.WaitAsync(Settings.Timeout);
        }

        Assert.NotNull(firstFailure);
        Assert.Equal(CaptureCode, ErrorCode(firstFailure));
        Assert.Equal(507, WireStatus(firstFailure));
        var failures = socket.Sent.Where(item => IsFailureTerminal(item)
            && ErrorCode(item) is not ("response_in_progress" or "previous_response_not_found")).ToArray();
        Assert.Single(failures);
        Assert.DoesNotContain(socket.Sent, item => Type(item) == "response.completed"
            && JsonDictionaryValue.String(Response(item), "id") == ResponseId);
        Assert.NotNull(missingHistory);
        Assert.NotNull(retainedHistory);
        Assert.Empty(context.FakeEndpoint.Calls.Skip(1));
        Assert.Equal(1, fixture.Source.Calls);
        Assert.Empty(fixture.Circuit.Failures);
        Assert.Equal(ChannelHealthStatus.Healthy, await fixture.Circuit.GetHealthStatusAsync(
            "test", "primary", true, Settings.Timeout));
        AssertCapturedFailure(Assert.Single(fixture.Logs.Rows.Where(row => row.RequestType == ProxyRequestTypes.Main)));
        fixture.Logs.Dispose();
        fixture.AssertSnapshotEmpty();
    }

    private static void AssertCapturedFailure(AuditRow row)
    {
        Assert.Equal(507, row.Status);
        var source = Assert.IsType<JsonElement>(row.Source);
        var capture = source.GetProperty("_opencodex_capture");
        Assert.Equal("received_frames", capture.GetProperty("representation").GetString());
        Assert.True(capture.GetProperty("prefix_limit_exceeded").GetBoolean());
        Assert.True(capture.GetProperty("record_count").GetInt64() >= 3);
        Assert.NotEmpty(Convert.FromBase64String(source.GetProperty("received_utf8_base64").GetString()!));
    }

    private static D Create(string? previous = null, bool generate = true)
    {
        var request = Request(stream: true);
        request["type"] = "response.create";
        request["generate"] = generate;
        if (previous is not null)
        {
            request["previous_response_id"] = previous;
            request["input"] = new List<object?>();
        }
        return request;
    }

    private static D Response(D payload) => JsonDictionaryValue.Object(payload, "response", WebSearchPayload.DeepCopyObject);
    private static D Error(D payload) => Type(payload) == "response.failed"
        ? JsonDictionaryValue.Object(Response(payload), "error", WebSearchPayload.DeepCopyObject)
        : JsonDictionaryValue.Object(payload, "error", WebSearchPayload.DeepCopyObject);
    private static string ErrorCode(D payload)
    {
        var nested = JsonDictionaryValue.String(Error(payload), "code");
        return nested.Length > 0 ? nested : JsonDictionaryValue.String(payload, "code");
    }
    private static int WireStatus(D payload) => WebSearchPayload.ToInt(
        JsonDictionaryValue.Get(payload, "status") ?? JsonDictionaryValue.Get(Error(payload), "status"), 0);
    private static bool IsFailureTerminal(D payload) => Type(payload) is "error" or "response.failed";
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<D> ReadUntil(TestSocket socket, Func<D, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            var item = await socket.Outgoing.Reader.ReadAsync(ct);
            if (predicate(item)) return item;
        }
    }

    private sealed record Limits(int CaptureBytes, int EventBytes, int DeltaCharacters, int Items, TimeSpan Timeout)
    {
        internal static Limits Read()
        {
            var result = new Limits(WsHistoryTestParameters.OutputCaptureMaxBytes,
                WsHistoryTestParameters.OutputEventMaxBytes,
                WsHistoryTestParameters.OutputDeltaCharacters,
                WsHistoryTestParameters.OutputItemCount,
                TimeSpan.FromMilliseconds(WsHistoryTestParameters.CaseTimeoutMilliseconds));
            if (result.CaptureBytes <= 0 || result.EventBytes <= result.DeltaCharacters || result.DeltaCharacters <= 0
                || result.Items < 2 || (long)result.DeltaCharacters * result.Items <= result.CaptureBytes
                || result.Timeout <= TimeSpan.Zero)
                throw new InvalidOperationException("The public Core/WS regression limits must cross C using legal individual frames.");
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "ocxp-core-ws-local-failure-" + Guid.NewGuid().ToString("N")));
        internal CancellationTokenSource Deadline { get; } = new(Settings.Timeout);
        internal FailingSource Source { get; } = new();
        internal SuccessfulAudit Logs { get; } = new();
        internal SseFrameOptions Frames { get; } = new()
        {
            MaxCaptureBytes = Settings.CaptureBytes, MaxEventBytes = Settings.EventBytes
        };
        internal ProxyAuditSpool Spool { get; }
        internal SnapshotStore Snapshots { get; }
        internal ClientConnectionCache Cache { get; }
        internal CountingCircuit Circuit { get; } = new(new ChannelCircuitBreakerService(
            1, Settings.Timeout, 1, null, () => DateTimeOffset.UnixEpoch));
        internal ProxyStreamService Streams { get; }
        internal ProxyEndpointService Endpoint { get; }
        private Guid Key { get; } = Guid.NewGuid();
        private MultiAgentRunStore Runs { get; }
        private MultiAgentClientStore Clients { get; }
        private NativeClientSessionStore Native { get; }
        private ProxyRouteDto Route { get; }

        internal Fixture()
        {
            Spool = new ProxyAuditSpool(new() { Directory = Path.Combine(_directory, "spool") });
            Snapshots = new SnapshotStore(Spool);
            Cache = new ClientConnectionCache(Snapshots);
            Runs = new MultiAgentRunStore(Path.Combine(_directory, "runs"));
            Clients = new MultiAgentClientStore(Runs, Path.Combine(_directory, "clients"));
            Native = new NativeClientSessionStore(Path.Combine(_directory, "native"));
            Route = new ProxyRouteDto(new D
            {
                ["id"] = "primary", ["type"] = ProtocolConverter.Responses, ["enabled"] = true,
                ["circuit_break_duration_seconds"] = checked((int)Math.Ceiling(Settings.Timeout.TotalSeconds))
            }, "fake", "upstream-fake", true, true);
            var tools = new NoTools();
            var history = WebSearchTestStore.Create();
            Streams = new ProxyStreamService(Source, Logs, tools, history, Spool, outputFrames: Frames);
            Endpoint = new ProxyEndpointService(Logs, new Requests(), new Identity(Key), new Routes(Route),
                new ChannelCapacityService(), Circuit, new ChannelAffinityService(), new NoImageFallback(),
                new NoNonStream(), Streams, tools, history, new MultiAgentRepeatGuard(), Spool);
        }

        internal ProxyStreamContext StreamContext(IProxyStreamWriter writer, CancellationToken ct)
        {
            var payload = Request(stream: true);
            return new ProxyStreamContext(Stopwatch.GetTimestamp(), Guid.NewGuid(), "core-local-failure", "test", Key,
                payload, payload, new D(payload, StringComparer.Ordinal), ProtocolConverter.Responses, Route,
                ProtocolConverter.Responses, "primary", "user", "upstream-fake", "fake",
                checked((int)Math.Ceiling(Settings.Timeout.TotalSeconds)),
                new ProxyRequestMetadata("GET", "/v1/responses", null, new Dictionary<string, string>()), writer, ct);
        }

        internal MultiAgentApiTestContext Context(Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler)
        {
            var context = new MultiAgentApiTestContext(handler, store: Runs, key: Key,
                clientStore: Clients, connectionCache: Cache, nativeStore: Native);
            context.Lifetime.CancelAfter(Settings.Timeout);
            return context;
        }

        internal void AssertSnapshotEmpty()
        {
            // Do not dispose Cache here: an unclosed WS history owner must remain observable.
            Spool.Dispose();
            Assert.Equal((0L, 0L, 0, 0), Snapshots.Usage);
        }

        public void Dispose()
        {
            Deadline.Cancel();
            Source.Release.TrySetResult();
            Cache.Dispose();
            Clients.Dispose();
            Native.Dispose();
            Runs.Dispose();
            Spool.Dispose();
            Deadline.Dispose();
            if (!Path.GetFileName(_directory).StartsWith("ocxp-core-ws-local-failure-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected Core/WS fixture cleanup path.");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FailingSource : IUpstreamClient
    {
        internal TaskCompletionSource Release { get; } = NewSignal();
        internal int Calls, OfferedFrames;
        public Task<D> PostJsonAsync(IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, CancellationToken ct) => throw new NotSupportedException("This regression is streaming only.");
        public IAsyncEnumerable<string> StreamJsonAsync(IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload, int defaultTimeout, CancellationToken ct) =>
            throw new NotSupportedException("Use the genuine decoded source-frame path.");
        public async IAsyncEnumerable<SseFrame> StreamEventsAsync(IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload, int defaultTimeout, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            OfferedFrames++;
            yield return SseFrame.FromJson(new D
            {
                ["type"] = "response.created", ["response"] = new D
                {
                    ["id"] = ResponseId, ["status"] = "in_progress", ["model"] = "upstream-fake",
                    ["output"] = new List<object?>()
                }
            });
            for (var index = 0; index < Settings.Items; index++)
            {
                if (index == 1) await Release.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                var frame = SseFrame.FromJson(new D
                {
                    ["type"] = "response.output_text.delta", ["output_index"] = 0, ["content_index"] = 0,
                    ["delta"] = new string('x', Settings.DeltaCharacters)
                });
                Assert.True(frame.Utf8Length <= Settings.EventBytes);
                OfferedFrames++;
                yield return frame;
            }
            // No extra throw and no terminal: real C admission rejects a complete offending frame first.
        }
    }

    private sealed record AuditRow(int Status, string RequestType, JsonElement? Source);
    private sealed class SuccessfulAudit : IProxyLogService, IDisposable
    {
        internal List<AuditRow> Rows { get; } = [];
        // The production log pipeline owns transferred body handles until commit. This
        // substitute must follow that contract so a zero-usage assertion detects real leaks.
        private readonly HashSet<ProxyLogBodyHandle> _owned = [];
        private void Adopt(ProxyLogBodyHandles? bodies)
        {
            foreach (var handle in new[] { bodies?.Headers, bodies?.Request, bodies?.UpstreamRequest })
                if (handle is not null) _owned.Add(handle);
        }
        private void Adopt(ProxyLogBodyHandle? handle)
        {
            if (handle is not null) _owned.Add(handle);
        }
        public void Dispose()
        {
            foreach (var handle in _owned) handle.Dispose();
            _owned.Clear();
        }
        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context)
        {
            Adopt(context.BodyHandles);
            return Guid.NewGuid();
        }
        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context) =>
            Adopt(context.UpstreamRequestBody);
        public Task CompleteLogAsync(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
        {
            Adopt(request.LogBodies);
            Adopt(context.UpstreamRequestBody);
            var reservation = request.SnapshotReservation
                ?? throw new InvalidOperationException("Successful audit must consume the explicit execution reservation.");
            JsonElement? Capture(object? value)
            {
                if (value is null) return null;
                using var body = reservation.CaptureAuditJson(value);
                var snapshot = body.ReadSnapshot();
                using var source = snapshot.Spool!.OpenRead();
                using var document = JsonDocument.Parse(source);
                return document.RootElement.Clone();
            }
            var captured = Capture(context.UpstreamResponse);
            _ = Capture(context.ResponsePayload ?? context.ErrorResponse);
            Rows.Add(new(context.StatusCode, context.RequestType, captured));
            return Task.CompletedTask;
        }
        public async Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request)
        {
            var id = Guid.NewGuid();
            await CompleteLogAsync(id, context, request);
            return id;
        }
        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context) =>
            throw new NotSupportedException("The real endpoint supplies its explicit request metadata.");
    }

    private sealed class CountingCircuit(ChannelCircuitBreakerService inner) : IChannelCircuitBreakerService
    {
        internal List<Exception> Failures { get; } = [];
        public Task<ChannelHealthStatus> GetHealthStatusAsync(string owner, string channel, bool enabled, TimeSpan? duration = null) =>
            inner.GetHealthStatusAsync(owner, channel, enabled, duration);
        public Task<bool> TryAcquireHalfOpenProbeAsync(string owner, string channel, TimeSpan? duration = null) =>
            inner.TryAcquireHalfOpenProbeAsync(owner, channel, duration);
        public Task ReleaseHalfOpenProbeAsync(string owner, string channel, TimeSpan? duration = null) =>
            inner.ReleaseHalfOpenProbeAsync(owner, channel, duration);
        public Task RecordSuccessAsync(string owner, string channel) => inner.RecordSuccessAsync(owner, channel);
        public Task ResetAsync(string owner, string channel) => inner.ResetAsync(owner, channel);
        public Task<bool> RecordFailureAsync(string owner, string channel, Exception exception, TimeSpan? duration = null)
        {
            Failures.Add(exception);
            return inner.RecordFailureAsync(owner, channel, exception, duration);
        }
    }

    private sealed class Requests : IProxyRequestService
    {
        public ProxyRequestState StartRequest() => new("core-ws-local-failure",
            checked((int)Math.Ceiling(Settings.Timeout.TotalSeconds)));
    }
    private sealed class Identity(Guid key) : IProxyIdentityContext
    {
        public bool IsAuthenticated => true;
        public ProxyIdentity Current { get; } = new(key, Guid.NewGuid(), "test", "user");
        public ProxyIdentity RequireIdentity() => Current;
    }
    private sealed class Routes(ProxyRouteDto route) : IProxyRouteService
    {
        public Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(string owner, string? model) =>
            Task.FromResult<IReadOnlyList<ProxyRouteDto>>([route]);
        public Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string owner) =>
            Task.FromResult(VisionTransferRoutesDto.NotConfigured());
        public Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string owner) =>
            Task.FromResult<IReadOnlyList<ProxyModelCapabilityDto>>([]);
    }
    private sealed class NoImageFallback : IProxyImageFallbackService
    {
        public Task<ProxyImageFallbackResult> RewriteAsync(ProxyImageFallbackContext context) =>
            throw new NotSupportedException("The test request contains no images.");
    }
    private sealed class NoNonStream : IProxyNonStreamService
    {
        public Task<ProxyNonStreamResult> SendAsync(ProxyNonStreamContext context) =>
            throw new NotSupportedException("The test model request is streaming.");
    }
    private sealed class NoTools : IWebSearchToolExecutor
    {
        public string CurrentMode() => WebSearchModes.Convert;
        public Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken ct) =>
            throw new NotSupportedException("The source produces no tool calls.");
    }
}
