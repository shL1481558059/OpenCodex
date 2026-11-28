using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;
using static OpenCodex.Api.Tests.MultiAgentClientResponseTests;

namespace OpenCodex.Api.Tests;

/// <summary>
/// First-terminal ownership, with no extra upstream exception in the repeated-terminal cases.
/// All deadlines come from the public root regression parameters; gates never use sleeps.
/// </summary>
public sealed class WsTerminalContractRegressionTests
{
    private static readonly TimeSpan CaseTimeout = ReadCaseTimeout();
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("completed", "failed")]
    [InlineData("failed", "completed")]
    public async Task GenericFirstTerminalOwnsResultEventsAndContinuation(string firstStatus, string secondStatus)
    {
        using var fixture = new Fixture();
        var streamFinished = NewSignal();
        var calls = 0;
        MultiAgentModelStreamWriter? firstWriter = null;
        const string id = "resp_generic_terminal_pair";
        var firstMarker = "FIRST_" + firstStatus;
        var secondMarker = "SECOND_" + secondStatus;
        using var context = fixture.Context(async endpoint =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return await Stream(endpoint, Events(TerminalEvent("resp_generic_continuation", "completed", "CONTINUED")));
            firstWriter = Assert.IsType<MultiAgentModelStreamWriter>(endpoint.StreamWriter);
            try
            {
                // Both are independently legal Responses terminals for the same id.
                // Do not add a throw: that would exercise the catch guard instead of the source bug.
                return await Stream(endpoint, Events(TerminalEvent(id, firstStatus, firstMarker),
                    TerminalEvent(id, secondStatus, secondMarker)));
            }
            finally { streamFinished.TrySetResult(); }
        });
        context.CatalogEnabled = false;
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        D? continuation = null;
        try
        {
            socket.ClientSend(Create());
            await socket.Until("response." + firstStatus, context.Lifetime.Token);
            await streamFinished.Task.WaitAsync(context.Lifetime.Token);
            // All first-stream sends have completed. Remove their queued copies before the probe.
            while (socket.Outgoing.Reader.TryRead(out _)) { }
            socket.ClientSend(Create(id, "NEXT_CLIENT_DELTA"));
            continuation = await ReadUntil(socket,
                item => Type(item) is "error" or "response.completed", context.Lifetime.Token);
        }
        finally
        {
            socket.ClientClose();
            await operation.WaitAsync(context.Lifetime.Token);
        }

        var terminals = socket.Sent.Where(item => IsTerminal(item) && ResponseId(item) == id).ToArray();
        var first = Assert.Single(terminals);
        Assert.Equal("response." + firstStatus, Type(first));
        Assert.Equal(firstStatus, JsonDictionaryValue.String(ResponseObject(first), "status"));
        var result = Assert.IsType<D>(firstWriter!.Result);
        Assert.Equal(firstStatus, JsonDictionaryValue.String(result, "status"));
        Assert.Contains(firstMarker, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(secondMarker, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.NotNull(continuation);
        if (firstStatus == "failed")
        {
            Assert.Equal("error", Type(continuation));
            Assert.Equal("previous_response_not_found",
                JsonDictionaryValue.String(JsonDictionaryValue.Object(continuation, "error", WebSearchPayload.DeepCopyObject), "code"));
            Assert.Equal(1, calls);
        }
        else
        {
            Assert.Equal("response.completed", Type(continuation));
            Assert.Equal(2, calls);
            var restored = JsonSerializer.Serialize(context.FakeEndpoint.Calls[1].Payload!["input"]);
            Assert.Contains(firstMarker, restored, StringComparison.Ordinal);
            Assert.DoesNotContain(secondMarker, restored, StringComparison.Ordinal);
            Assert.Contains("NEXT_CLIENT_DELTA", restored, StringComparison.Ordinal);
        }
        fixture.AssertSnapshotEmpty();
    }

    [Theory]
    [InlineData("completed", "failed")]
    [InlineData("failed", "completed")]
    public async Task ModelWriterKeepsFirstTerminalResultAndInvokesTerminalCallbackOnce(string firstStatus, string secondStatus)
    {
        using var deadline = new CancellationTokenSource(CaseTimeout);
        var seen = new List<D>();
        var writer = new MultiAgentModelStreamWriter((payload, _) =>
        {
            seen.Add(WebSearchPayload.DeepCopyObject(payload));
            return Task.CompletedTask;
        });
        var firstMarker = "WRITER_FIRST_" + firstStatus;
        var secondMarker = "WRITER_SECOND_" + secondStatus;
        await writer.WriteLinesAsync(Lines(TerminalEvent("resp_writer_pair", firstStatus, firstMarker),
                TerminalEvent("resp_writer_pair", secondStatus, secondMarker)),
            _ => false, () => 0, deadline.Token);

        Assert.Equal("response." + firstStatus, Type(Assert.Single(seen.Where(IsTerminal))));
        var result = Assert.IsType<D>(writer.Result);
        Assert.Equal(firstStatus, JsonDictionaryValue.String(result, "status"));
        Assert.Contains(firstMarker, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(secondMarker, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrewarmRejectsScalarOrObjectInputInsteadOfCompletingEmptyHistory(bool objectInput)
    {
        using var fixture = new Fixture();
        using var context = fixture.Context(endpoint => Stream(endpoint, Events(
            TerminalEvent("resp_unexpected_model", "completed", "UNEXPECTED_MODEL"))));
        context.CatalogEnabled = false;
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        D? outcome = null;
        try
        {
            var request = Create();
            request["generate"] = false;
            request["input"] = objectInput ? new D() : 123;
            socket.ClientSend(request);
            outcome = await ReadUntil(socket,
                item => Type(item) is "error" or "response.completed", context.Lifetime.Token);
        }
        finally
        {
            socket.ClientClose();
            await operation.WaitAsync(context.Lifetime.Token);
        }

        Assert.NotNull(outcome);
        Assert.Equal("error", Type(outcome));
        Assert.Equal(400, WebSearchPayload.ToInt(JsonDictionaryValue.Get(outcome, "status"), 0));
        Assert.Equal("invalid_request",
            JsonDictionaryValue.String(JsonDictionaryValue.Object(outcome, "error", WebSearchPayload.DeepCopyObject), "code"));
        Assert.Empty(context.FakeEndpoint.Calls);
        Assert.DoesNotContain(socket.Sent, item => Type(item) is "response.created" or "response.completed");
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public void HistoryAddRefRollsBackEarlierClonesWhenLastInputLeaseFaults()
    {
        using var fixture = new Fixture();
        using var connection = fixture.Cache.Open(Guid.NewGuid());
        using (var prepared = connection.Prepare(TwoInputRequest(), null, generate: false, cancellationToken: default))
        {
            var response = ResponseObject(TerminalEvent("resp_addref_fault", "completed", "ORIGINAL"));
            prepared.ReserveKnownResponse(response, default);
            prepared.Commit(response, default);
        }
        using var version = Assert.IsType<ClientHistoryVersion>(connection.AcquireHistory("resp_addref_fault"));
        Assert.Equal(2, version.InputCount);
        // Explicit internal fault injection, NOT a valid public ownership operation.
        // Metadata and the first input AddRef succeed before the disposed last input throws.
        version.Input[^1].Dispose();
        Assert.Throws<ObjectDisposedException>(() => version.AddRef());
        version.Dispose();
        connection.Dispose();
        fixture.Cache.Dispose();
        fixture.Spool.Dispose();
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public void PreparedCommitRollsBackEarlierClonesWhenLastInputLeaseFaults()
    {
        using var fixture = new Fixture();
        using var connection = fixture.Cache.Open(Guid.NewGuid());
        using var prepared = connection.Prepare(TwoInputRequest(), null, generate: false, cancellationToken: default);
        var response = ResponseObject(TerminalEvent("resp_commit_clone_fault", "completed", "NOT_COMMITTED"));
        prepared.ReserveKnownResponse(response, default);
        // Deliberately corrupt only this prepared owner's last lease, after reserving known output.
        // This deterministically represents a later AddRef failure, not normal public usage.
        var inputField = Assert.Single(typeof(PreparedClientRequest).GetFields(InstanceFields)
            .Where(field => field.Name == "_input" && field.FieldType == typeof(SnapshotLease[])));
        var input = Assert.IsType<SnapshotLease[]>(inputField.GetValue(prepared));
        Assert.Equal(2, input.Length);
        input[^1].Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Commit(response, default));
        Assert.False(prepared.Committed);
        Assert.False(connection.Knows("resp_commit_clone_fault"));
        prepared.Dispose();
        connection.Dispose();
        fixture.Cache.Dispose();
        fixture.Spool.Dispose();
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public async Task NativeTerminalAttemptFollowedByExecutorFaultDoesNotSendSecondTerminal()
    {
        using var fixture = new Fixture();
        var terminalAttempted = NewSignal();
        var releaseFault = NewSignal();
        var faultInjected = NewSignal();
        const string id = "resp_native_attempt_once";
        using var context = fixture.Context(async endpoint =>
        {
            var slot = CaptureNativeEmit(Assert.IsType<MultiAgentModelStreamWriter>(endpoint.StreamWriter));
            slot.Replace(async item =>
            {
                await slot.Original(item);
                if (Type(item) != "response.completed") return;
                // The real Root Send has returned: its wire terminal was already attempted.
                terminalAttempted.TrySetResult();
                await releaseFault.Task.WaitAsync(fixture.Deadline.Token);
                faultInjected.TrySetResult();
                throw new InvalidOperationException("Injected executor fault after the terminal attempt.");
            });
            return await Stream(endpoint, Events(TerminalEvent(id, "completed", "ALREADY_SENT")));
        }, native: true);
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            socket.ClientSend(NativeCreate());
            await terminalAttempted.Task.WaitAsync(context.Lifetime.Token);
            // A gate can be signalled just before Execute returns its pending Task to the
            // reader. This protocol round-trip proves the active assignment has happened.
            await UnknownInterruptBarrier(socket, "catch-active-assigned", context.Lifetime.Token);
            var active = CapturedActiveTask(operation);
            Assert.False(active.IsCompleted);
            Assert.False(context.Lifetime.IsCancellationRequested);
            releaseFault.TrySetResult();
            await active.WaitAsync(context.Lifetime.Token);
            // A following protocol reply also proves the reader is still usable; no early close
            // may cancel the source's second terminal before the test records it.
            await UnknownInterruptBarrier(socket, "catch-observation-barrier", context.Lifetime.Token);
            Assert.True(faultInjected.Task.IsCompletedSuccessfully);
        }
        finally
        {
            releaseFault.TrySetResult();
            socket.ClientClose();
            await operation.WaitAsync(context.Lifetime.Token);
        }

        var first = Assert.Single(socket.Sent.Where(IsTerminal));
        Assert.Equal("response.completed", Type(first));
        Assert.Equal(id, ResponseId(first));
        Assert.Contains("ALREADY_SENT", JsonSerializer.Serialize(first), StringComparison.Ordinal);
        Assert.DoesNotContain(socket.Sent, item => Type(item) is "error" or "response.failed");
        Assert.Single(context.FakeEndpoint.Calls);
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public async Task InterruptRechecksTerminalThatCompletesWhileAwaitingActiveRequest()
    {
        using var fixture = new Fixture();
        var beforeRootTerminal = NewSignal();
        var releaseTerminal = NewSignal();
        var responseCancelled = NewSignal();
        const string id = "resp_native_interrupt_terminal_race";
        using var context = fixture.Context(async endpoint =>
        {
            var slot = CaptureNativeEmit(Assert.IsType<MultiAgentModelStreamWriter>(endpoint.StreamWriter));
            slot.Replace(async item =>
            {
                if (Type(item) == "response.completed")
                {
                    // Native Send has already checked the response token. Root Send has not
                    // yet registered the terminal. Keep the original payload and delegate.
                    beforeRootTerminal.TrySetResult();
                    await releaseTerminal.Task.WaitAsync(fixture.Deadline.Token);
                }
                await slot.Original(item);
            });
            // Keep the token registration alive until the native emit gate is reached.
            // The handler otherwise returns before Native publishes its final terminal.
            var result = await Stream(endpoint, Events(TerminalEvent(id, "completed", "COMMIT_WINS")));
            fixture.ResponseCancellationRegistration = endpoint.CancellationToken.Register(() => responseCancelled.TrySetResult());
            return result;
        }, native: true);
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        try
        {
            socket.ClientSend(NativeCreate());
            await beforeRootTerminal.Task.WaitAsync(context.Lifetime.Token);
            await UnknownInterruptBarrier(socket, "interrupt-active-assigned", context.Lifetime.Token);
            var active = CapturedActiveTask(operation);
            Assert.False(active.IsCompleted);
            Assert.DoesNotContain(socket.Sent, IsTerminal);
            socket.ClientSend(Interrupt(id));
            // This cannot come from closing the connection: the Root connection token stays live.
            await responseCancelled.Task.WaitAsync(context.Lifetime.Token);
            Assert.False(context.Lifetime.IsCancellationRequested);
            releaseTerminal.TrySetResult();
            await active.WaitAsync(context.Lifetime.Token);
            var accepted = await socket.Until("response.interrupt.accepted", context.Lifetime.Token);
            Assert.Equal(id, JsonDictionaryValue.String(accepted, "response_id"));
            // The reader processes this only after the complete first interrupt branch.
            await UnknownInterruptBarrier(socket, "interrupt-observation-barrier", context.Lifetime.Token);
        }
        finally
        {
            releaseTerminal.TrySetResult();
            socket.ClientClose();
            await operation.WaitAsync(context.Lifetime.Token);
        }

        var first = Assert.Single(socket.Sent.Where(IsTerminal));
        Assert.Equal("response.completed", Type(first));
        Assert.Equal(id, ResponseId(first));
        Assert.Contains("COMMIT_WINS", JsonSerializer.Serialize(first), StringComparison.Ordinal);
        Assert.DoesNotContain(socket.Sent, item => Type(item) is "error" or "response.failed" or "response.incomplete");
        Assert.Single(context.FakeEndpoint.Calls);
        fixture.AssertSnapshotEmpty();
    }

    [Fact]
    public async Task MarkerSnapshotIoFailureBeforeModelReleasesConnectionReservationAndLegacyLease()
    {
        var ioAttempted = NewSignal();
        var failMarkerStorage = 0;
        var ioAttempts = 0;
        Task? serviceOperation = null;
        SnapshotReservation? capturedIdentityReservation = null;
        using var fixture = new Fixture((path, options) =>
        {
            if (Volatile.Read(ref failMarkerStorage) == 0) return new FileStream(path, options);
            // The initial connection identity has already been captured successfully.
            // Fault the later request-owned marker's real snapshot CreateFile boundary.
            // Capture its explicit owner before the exception can unwind the task box.
            capturedIdentityReservation ??= CapturedIdentityReservation(serviceOperation
                ?? throw new InvalidOperationException("The marker IO fault preceded the running fixture task."));
            Interlocked.Increment(ref ioAttempts);
            ioAttempted.TrySetResult();
            throw new IOException("Injected request marker snapshot IO failure.");
        });
        using var context = fixture.Context(endpoint => Stream(endpoint, Events(
            TerminalEvent("resp_unexpected_io_model", "completed", "MODEL_MUST_NOT_RUN"))));
        // No native identity and an enabled catalog select the real legacy runtime/lease path.
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        serviceOperation = operation;
        try
        {
            Volatile.Write(ref failMarkerStorage, 1);
            socket.ClientSend(Create());
            await ioAttempted.Task.WaitAsync(context.Lifetime.Token);
            // Unregistered marker admission may reject before Execute starts and close
            // the connection. No later interrupt acknowledgement is owed on that path.
            // Collect its real termination without first closing/cancelling the client.
            var connectionFailure = await Record.ExceptionAsync(() => operation.WaitAsync(context.Lifetime.Token));
            if (connectionFailure is not null) Assert.IsType<SnapshotPersistenceException>(connectionFailure);
            Assert.False(context.Lifetime.IsCancellationRequested);

            Assert.True(ioAttempts > 0);
            Assert.NotNull(capturedIdentityReservation);
            Assert.Empty(context.FakeEndpoint.Calls);
            Assert.DoesNotContain(socket.Sent, item => Type(item) == "response.completed");
            var error = Assert.Single(socket.Sent.Where(item => Type(item) == "error"));
            Assert.Equal(507, WebSearchPayload.ToInt(JsonDictionaryValue.Get(error, "status"), 0));
            var details = JsonDictionaryValue.Object(error, "error", WebSearchPayload.DeepCopyObject);
            Assert.Equal("snapshot_write_failed", JsonDictionaryValue.String(details, "code"));
            Assert.Equal("insufficient_storage", JsonDictionaryValue.String(details, "type"));
            Assert.NotEqual(System.Net.WebSockets.WebSocketState.Open, socket.State);
            // These assertions precede ALL fixture repair. The former source leaked here
            // when a fallible finally RememberId prevented lease/disposal work from running.
            fixture.AssertSnapshotEmpty();
            await fixture.AssertLegacyRequestLeaseAvailable(context.Key, fixture.Deadline.Token);
        }
        finally
        {
            // Deliberate fault-injection cleanup AFTER the ownership assertions. A red
            // baseline may have stranded this request's explicit credits; release that
            // captured owner so the test does not strand quota or private-file guards.
            capturedIdentityReservation?.Dispose();
            fixture.Cache.Dispose();
            fixture.Spool.Dispose();
        }
    }

    [Fact]
    public async Task LegacyKnownFailedIdStillRejectsInjectionAsCompletedAfterNextTurn()
    {
        using var fixture = new Fixture();
        var firstModelStarted = NewSignal();
        var releaseFirstFailure = NewSignal();
        var calls = 0;
        using var context = fixture.Context(async endpoint =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstModelStarted.TrySetResult();
                await releaseFirstFailure.Task.WaitAsync(endpoint.CancellationToken);
                // Non-Proxy exception deliberately reaches the API outer failed path.
                // The legacy runtime has already emitted/registered response.created.
                throw new InvalidOperationException("Injected legacy failure after the created identity.");
            }
            return await Stream(endpoint, Events(
                TerminalEvent("resp_legacy_marker_next_model", "completed", "NEXT_LEGACY_TURN")));
        });
        using var socket = context.Socket();
        var operation = context.Service.ResponsesWebSocket();
        var failedId = "";
        var completedId = "";
        D? failedTerminal = null;
        D? completedTerminal = null;
        D? lateFailedInjection = null;
        D? unknownInjection = null;
        var entriesBeforeInjection = 0;
        var entriesAfterInjection = 0;
        const string unknownId = "resp_unknown_failure_marker";
        D Injection(string id) => new()
        {
            ["type"] = "response.inject", ["response_id"] = id,
            ["input"] = new List<object?>
            {
                new D { ["type"] = "function_call_output", ["call_id"] = "closed-failure-call", ["output"] = "NOT_ACCEPTED" }
            }
        };
        try
        {
            var first = Request();
            first["type"] = "response.create";
            socket.ClientSend(first);
            var created = await socket.Until("response.created", context.Lifetime.Token);
            failedId = ResponseId(created);
            await firstModelStarted.Task.WaitAsync(context.Lifetime.Token);
            await UnknownInterruptBarrier(socket, "legacy-first-failure-active", context.Lifetime.Token);
            var firstActive = CapturedActiveTask(operation);
            Assert.False(firstActive.IsCompleted);
            releaseFirstFailure.TrySetResult();
            failedTerminal = await ReadUntil(socket,
                item => Type(item) == "response.failed" && ResponseId(item) == failedId, context.Lifetime.Token);
            await firstActive.WaitAsync(context.Lifetime.Token);
            Assert.False(context.Lifetime.IsCancellationRequested);

            // Resume the interrupted legacy task without queuing a third user turn: the
            // focus here is the failed-id completion marker, not runtime scheduling.
            var second = Request();
            second["type"] = "response.create";
            second["input"] = new List<object?>();
            socket.ClientSend(second);
            completedTerminal = await socket.Until("response.completed", context.Lifetime.Token);
            completedId = ResponseId(completedTerminal);
            await UnknownInterruptBarrier(socket, "legacy-second-marker-active", context.Lifetime.Token);
            await CapturedActiveTask(operation).WaitAsync(context.Lifetime.Token);
            await fixture.AssertLegacyRequestLeaseAvailable(context.Key, fixture.Deadline.Token);
            entriesBeforeInjection = fixture.Cache.Usage.Entries;

            // A new completed turn has replaced the temporary terminalResponseId latch.
            // The first failed id must still be a known completed marker, not successful history.
            socket.ClientSend(Injection(failedId));
            lateFailedInjection = await ReadUntil(socket,
                item => Type(item) == "response.inject.failed" && JsonDictionaryValue.String(item, "response_id") == failedId,
                context.Lifetime.Token);
            socket.ClientSend(Injection(unknownId));
            unknownInjection = await ReadUntil(socket,
                item => Type(item) == "response.inject.failed" && JsonDictionaryValue.String(item, "response_id") == unknownId,
                context.Lifetime.Token);
            entriesAfterInjection = fixture.Cache.Usage.Entries;
        }
        finally
        {
            releaseFirstFailure.TrySetResult();
            socket.ClientClose();
            await operation.WaitAsync(context.Lifetime.Token);
        }

        Assert.NotEmpty(failedId);
        Assert.NotEqual(failedId, completedId);
        Assert.Equal("failed", JsonDictionaryValue.String(ResponseObject(Assert.IsType<D>(failedTerminal)), "status"));
        Assert.Equal("completed", JsonDictionaryValue.String(ResponseObject(Assert.IsType<D>(completedTerminal)), "status"));
        Assert.Single(socket.Sent, item => IsTerminal(item) && ResponseId(item) == failedId);
        Assert.Single(socket.Sent, item => IsTerminal(item) && ResponseId(item) == completedId);
        var late = Assert.IsType<D>(lateFailedInjection);
        Assert.Equal("response_already_completed",
            JsonDictionaryValue.String(JsonDictionaryValue.Object(late, "error", WebSearchPayload.DeepCopyObject), "code"));
        var unknown = Assert.IsType<D>(unknownInjection);
        Assert.Equal("response_not_found",
            JsonDictionaryValue.String(JsonDictionaryValue.Object(unknown, "error", WebSearchPayload.DeepCopyObject), "code"));
        Assert.Equal(JsonSerializer.Serialize(Injection(failedId)["input"]), JsonSerializer.Serialize(late["input"]));
        Assert.Equal(JsonSerializer.Serialize(Injection(unknownId)["input"]), JsonSerializer.Serialize(unknown["input"]));
        Assert.Equal(2, entriesBeforeInjection);
        Assert.Equal(entriesBeforeInjection, entriesAfterInjection); // Unknown control ids must not allocate entries.
        Assert.Equal(2, calls);
        Assert.Equal(2, context.FakeEndpoint.Calls.Count);
        Assert.DoesNotContain(socket.Sent, item => Type(item) == "response.inject.created");
        fixture.AssertSnapshotEmpty();
    }

    private static D Create(string previous = "", string input = "FIRST_CLIENT_INPUT")
    {
        var request = Request();
        request["type"] = "response.create";
        request["input"] = input;
        if (previous.Length > 0) request["previous_response_id"] = previous;
        return request;
    }

    private static D NativeCreate()
    {
        var request = ClientRequest();
        request["type"] = "response.create";
        return request;
    }

    private static D TwoInputRequest() => new()
    {
        ["model"] = "fake",
        ["input"] = new List<object?>
        {
            new D { ["type"] = "message", ["role"] = "user", ["content"] = "EARLIER_INPUT" },
            new D { ["type"] = "message", ["role"] = "user", ["content"] = "FAULTED_LAST_INPUT" }
        }
    };

    private static D TerminalEvent(string id, string status, string marker)
    {
        var response = new D
        {
            ["id"] = id, ["object"] = "response", ["status"] = status, ["model"] = "fake",
            ["output"] = status == "completed" ? new List<object?> { Message(marker) } : new List<object?>(),
            ["usage"] = new D { ["input_tokens"] = 1, ["output_tokens"] = 1 }
        };
        if (status == "failed")
            response["error"] = new D { ["type"] = "upstream_error", ["code"] = "fixture_failure", ["message"] = marker };
        return new D { ["type"] = "response." + status, ["response"] = response };
    }

    private static D ResponseObject(D item) => Assert.IsType<D>(JsonDictionaryValue.Get(item, "response"));
    private static string ResponseId(D item) => JsonDictionaryValue.String(ResponseObject(item), "id");
    private static bool IsTerminal(D item) => Type(item) is "response.completed" or "response.failed" or "response.incomplete";
    private static D Interrupt(string id) => new()
    {
        ["type"] = "response.interrupt", ["response_id"] = id, ["mode"] = "discard_partial_items"
    };
    private static bool HasExplicitSnapshotIoError(D item)
    {
        var error = Type(item) == "response.failed"
            ? JsonDictionaryValue.Object(ResponseObject(item), "error", WebSearchPayload.DeepCopyObject)
            : JsonDictionaryValue.Object(item, "error", WebSearchPayload.DeepCopyObject);
        if (JsonDictionaryValue.String(error, "code") != "snapshot_write_failed") return false;
        return Type(item) == "error" && WebSearchPayload.ToInt(JsonDictionaryValue.Get(item, "status"), 0) == 507
            || Type(item) == "response.failed" && JsonDictionaryValue.String(error, "type") == "insufficient_storage";
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<D> ReadUntil(TestSocket socket, Func<D, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            var item = await socket.Outgoing.Reader.ReadAsync(ct);
            if (predicate(item)) return item;
        }
    }

    private static async Task UnknownInterruptBarrier(TestSocket socket, string id, CancellationToken ct)
    {
        socket.ClientSend(Interrupt(id));
        var reply = await ReadUntil(socket,
            item => Type(item) == "response.interrupt.failed" && JsonDictionaryValue.String(item, "response_id") == id, ct);
        Assert.Equal("response_not_found",
            JsonDictionaryValue.String(JsonDictionaryValue.Object(reply, "error", WebSearchPayload.DeepCopyObject), "code"));
    }

    private static async IAsyncEnumerable<string> Lines(params D[] items)
    {
        foreach (var item in items)
        {
            yield return "data: " + JsonSerializer.Serialize(item) + "\n\n";
            await Task.Yield();
        }
    }

    private static TimeSpan ReadCaseTimeout()
    {
        return TimeSpan.FromMilliseconds(WsHistoryTestParameters.CaseTimeoutMilliseconds);
    }

    // Test-instance-only boundary injection. It does not change Root or Native source,
    // cancellation checks, responses, or the actual WebSocket Send delegate.
    private sealed record CapturedEmit(object Owner, FieldInfo Field, Func<D, Task> Original)
    {
        internal void Replace(Func<D, Task> wrapper) => Field.SetValue(Owner, wrapper);
    }

    private static CapturedEmit CaptureNativeEmit(MultiAgentModelStreamWriter writer)
    {
        var callback = Assert.Single(writer.GetType().GetFields(InstanceFields)
            .Where(field => field.FieldType == typeof(Func<D, CancellationToken, Task>))
            .Select(field => field.GetValue(writer)).OfType<Func<D, CancellationToken, Task>>());
        Assert.NotNull(callback.Target);
        var matches = new List<CapturedEmit>();
        foreach (var owner in CompilerCaptures(callback.Target))
            foreach (var field in owner.GetType().GetFields(InstanceFields))
                if (field.FieldType == typeof(Func<D, Task>) && field.Name.Contains("emit", StringComparison.OrdinalIgnoreCase)
                    && field.GetValue(owner) is Func<D, Task> emit)
                    matches.Add(new CapturedEmit(owner, field, emit));
        // A missing or ambiguous boundary is a fixture failure, never a skip/fallback.
        return Assert.Single(matches);
    }

    private static Task CapturedActiveTask(Task serviceOperation)
    {
        // Read once while the explicit emit gate is blocked. This is not task-status polling.
        // Awaiting the request task prevents client-close cancellation from hiding a late terminal.
        var state = CapturedServiceState(serviceOperation);
        var activeField = Assert.Single(state.GetType().GetFields(InstanceFields)
            .Where(field => typeof(Task).IsAssignableFrom(field.FieldType)
                && field.Name.Contains("active", StringComparison.OrdinalIgnoreCase)));
        return Assert.IsAssignableFrom<Task>(activeField.GetValue(state));
    }

    private static object CapturedServiceState(Task serviceOperation)
    {
        // Fixed .NET 10 fixture constraint: fail clearly if its running Task box changes.
        // This introspection is only for test instances, never a production test hook.
        var stateFields = serviceOperation.GetType().GetFields(InstanceFields)
            .Where(field => field.Name == "StateMachine" && typeof(IAsyncStateMachine).IsAssignableFrom(field.FieldType));
        var stateField = Assert.Single(stateFields);
        return stateField.GetValue(serviceOperation)
            ?? throw new InvalidOperationException("The service state machine was already cleared.");
    }

    private static SnapshotReservation CapturedIdentityReservation(Task serviceOperation)
    {
        var matches = new List<SnapshotReservation>();
        foreach (var owner in CompilerCaptures(CapturedServiceState(serviceOperation)))
            foreach (var field in owner.GetType().GetFields(InstanceFields))
                if (field.FieldType == typeof(SnapshotReservation)
                    && field.Name.Contains("identityReservation", StringComparison.OrdinalIgnoreCase)
                    && field.GetValue(owner) is SnapshotReservation reservation)
                    matches.Add(reservation);
        return Assert.Single(matches);
    }

    private static IEnumerable<object> CompilerCaptures(object root)
    {
        var captures = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        captures.Push(root);
        while (captures.TryPop(out var owner))
        {
            if (!visited.Add(owner)) continue;
            yield return owner;
            foreach (var field in owner.GetType().GetFields(InstanceFields))
            {
                var value = field.GetValue(owner);
                var child = value is Delegate nested ? nested.Target : value;
                if (child is not null && child.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    captures.Push(child);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "ocxp-ws-terminal-contract-" + Guid.NewGuid().ToString("N")));
        internal CancellationTokenSource Deadline { get; } = new(CaseTimeout);
        internal ProxyAuditSpool Spool { get; }
        internal SnapshotStore Store { get; }
        internal ClientConnectionCache Cache { get; }
        private MultiAgentRunStore Runs { get; }
        private MultiAgentClientStore Clients { get; }
        private NativeClientSessionStore Native { get; }
        internal CancellationTokenRegistration ResponseCancellationRegistration;

        internal Fixture(Func<string, FileStreamOptions, Stream>? createFile = null)
        {
            var options = new ProxyAuditSpoolOptions { Directory = Path.Combine(_directory, "spool") };
            Spool = createFile is null ? new ProxyAuditSpool(options) : new ProxyAuditSpool(options) { CreateFile = createFile };
            Store = new SnapshotStore(Spool);
            Cache = new ClientConnectionCache(Store);
            Runs = new MultiAgentRunStore(Path.Combine(_directory, "runs"));
            Clients = new MultiAgentClientStore(Runs, Path.Combine(_directory, "clients"));
            Native = new NativeClientSessionStore(Path.Combine(_directory, "native"));
        }

        internal MultiAgentApiTestContext Context(Func<ProxyEndpointContext, Task<ProxyEndpointResult>> handler, bool native = false)
        {
            var context = new MultiAgentApiTestContext(handler, store: Runs, session: "terminal-contract-thread",
                clientStore: Clients, connectionCache: Cache, nativeStore: Native);
            context.Lifetime.CancelAfter(CaseTimeout);
            if (native) Identify(context, "terminal-contract-thread");
            return context;
        }

        internal async Task AssertLegacyRequestLeaseAvailable(Guid owner, CancellationToken ct)
        {
            var run = await Runs.GetAsync($"{owner:N}:terminal-contract-thread", "",
                () => throw new InvalidOperationException("The IO fixture must reuse its already-created legacy run."), ct, owner);
            await using var observation = await Runs.AcquireRequestAsync(run, ct);
        }

        internal void AssertSnapshotEmpty()
        {
            // Every caller has finished all snapshot admission/reading. End this fixture
            // spool lifetime so its idle owner.lock charge is released. This only stops
            // admission; active leases/reservations remain charged and still fail below.
            // Do NOT dispose Cache here: that would hide an unclosed connection owner.
            Spool.Dispose();
            Assert.Equal((0L, 0L, 0L, 0), Store.Usage);
        }

        public void Dispose()
        {
            ResponseCancellationRegistration.Dispose();
            Deadline.Cancel();
            Cache.Dispose();
            Clients.Dispose();
            Native.Dispose();
            Runs.Dispose();
            Spool.Dispose();
            Deadline.Dispose();
            // Only the GUID directory created by this fixture is a deletion target.
            if (!Path.GetFileName(_directory).StartsWith("ocxp-ws-terminal-contract-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected terminal fixture cleanup path.");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
