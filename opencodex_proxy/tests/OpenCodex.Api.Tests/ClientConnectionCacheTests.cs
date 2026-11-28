using System.Text.Json;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;
using static OpenCodex.Api.Tests.MultiAgentApiTestContext;

namespace OpenCodex.Api.Tests;

public sealed class ClientConnectionCacheTests : IDisposable
{
    private static readonly CacheTestParameters Settings = CacheTestParameters.Load();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ocxp-client-history-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ProxyAuditSpool _spool;
    private readonly SnapshotStore _store;
    private readonly ClientConnectionCache _cache;

    public ClientConnectionCacheTests()
    {
        _spool = new ProxyAuditSpool(new() { Directory = _directory });
        _store = new SnapshotStore(_spool);
        _cache = new ClientConnectionCache(_store);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _spool.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RootSnapshotStoreOwnsTheConnectionHistory()
    {
        Assert.Same(_store, _cache.Store);
        using var scope = _cache.Open(Guid.NewGuid());
        Remember(scope, "root-budget", RequestValue(), "stored");
        Assert.True(UsedCredits() > 0);
        Assert.True(scope.Knows("root-budget"));
        Assert.True(scope.IsCompleted("root-budget"));
        CloseAndAssertEmpty();
    }

    [Fact]
    public void RootHistoricalRequestPersistsAndDeltaSharesOriginalBlob()
    {
        using var scope = _cache.Open(Guid.NewGuid());
        var request = RequestValue("");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(request).Length;
        var text = new string('x', checked(Settings.HistoricalRequestBytes - overhead));
        request["input"] = text;
        Assert.Equal((long)Settings.HistoricalRequestBytes, JsonSerializer.SerializeToUtf8Bytes(request).LongLength);
        Remember(scope, "large", request, "first");

        using (var first = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("large")))
        {
            Assert.Equal(new[] { text }, InputTexts(first.MaterializeRequest()));
            var before = _store.Usage.PhysicalBytes;
            Remember(scope, "delta", RequestValue("next"), "second", first);
            using var second = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("delta"));
            Assert.Equal(new[] { text, "next" }, InputTexts(second.MaterializeRequest()));
            Assert.Equal(1, first.InputCount);
            Assert.Equal(2, second.InputCount);
            Assert.Equal("first", first.MaterializeResponse()["value"]);
            Assert.Equal("second", second.MaterializeResponse()["value"]);
            // A small delta must not persist a second physical copy of the large prefix.
            Assert.InRange(_store.Usage.PhysicalBytes - before, 1L, Settings.HistoricalRequestBytes - 1L);
            Assert.True(scope.Knows("large"));
            Assert.True(scope.IsCompleted("large"));
            Assert.True(UsedCredits() > 0);
        }
        CloseAndAssertEmpty();
    }

    [Fact]
    public void CommittedVersionsDoNotRetainMutableRequestOrResponseGraphs()
    {
        using var scope = _cache.Open(Guid.NewGuid());
        var request = RequestValue("original");
        var response = Remember(scope, "immutable", request, "original");
        request["input"] = "changed request";
        response["value"] = "changed response";
        using (var history = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("immutable")))
        {
            Assert.Equal(new[] { "original" }, InputTexts(history.MaterializeRequest()));
            Assert.Equal("original", history.MaterializeResponse()["value"]);
            var materialized = history.MaterializeRequest();
            Assert.IsType<D>(Assert.Single(JsonDictionaryValue.List(materialized, "input")))["content"] = "changed materialization";
            Assert.Equal(new[] { "original" }, InputTexts(history.MaterializeRequest()));
        }
        CloseAndAssertEmpty();
    }

    [Fact]
    public void ChurnRetainsEveryCommittedIdUntilConnectionClose()
    {
        using (var scope = _cache.Open(Guid.NewGuid()))
        {
            for (var index = 0; index < Settings.CacheHistoryVersions; index++)
            {
                Remember(scope, "id" + index, RequestValue("request" + index), "response" + index);
                Assert.Equal(index + 1, _cache.Usage.Entries);
                Assert.True(UsedCredits() > 0);
            }
            for (var index = 0; index < Settings.CacheHistoryVersions; index++)
            {
                Assert.True(scope.Knows("id" + index));
                Assert.True(scope.IsCompleted("id" + index));
            }
            using var first = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("id0"));
            using var last = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("id" + (Settings.CacheHistoryVersions - 1)));
            Assert.Equal("response0", first.MaterializeResponse()["value"]);
            Assert.Equal("response" + (Settings.CacheHistoryVersions - 1), last.MaterializeResponse()["value"]);
        }
        CloseAndAssertEmpty();
    }

    [Fact]
    public void TenantAndConnectionIsolationAreNotInferredFromStringKeys()
    {
        var tenant = Guid.NewGuid();
        using var a = _cache.Open(tenant);
        using var a2 = _cache.Open(tenant);
        using var b = _cache.Open(Guid.NewGuid());
        Remember(a, "same", RequestValue(), "tenant-a");
        Remember(a, "a-only", RequestValue(), "only-this-connection");
        Remember(b, "same", RequestValue(), "tenant-b");
        using (var historyA = Assert.IsType<ClientHistoryVersion>(a.AcquireHistory("same")))
        using (var historyB = Assert.IsType<ClientHistoryVersion>(b.AcquireHistory("same")))
        {
            Assert.Equal("tenant-a", historyA.MaterializeResponse()["value"]);
            Assert.Equal("tenant-b", historyB.MaterializeResponse()["value"]);
        }
        Assert.Null(a2.AcquireHistory("same"));
        Assert.Null(a2.AcquireHistory("a-only"));
        Assert.Null(b.AcquireHistory("a-only"));
        Assert.False(a2.Knows("same"));
        Assert.False(b.Knows("a-only"));
        Assert.Equal(3, _cache.Usage.Entries);
        Assert.True(UsedCredits() > 0);
        CloseAndAssertEmpty();
    }

    [Fact]
    public void SharedHistoryAddsNewVersionsWithoutEvictingOldId()
    {
        using var a = _cache.Open(Guid.NewGuid());
        using var b = _cache.Open(Guid.NewGuid());
        Remember(a, "old", RequestValue("old request"), "old response");
        Remember(a, "new", RequestValue("new request"), "new response");
        Assert.True(a.Knows("old"));
        Assert.True(a.IsCompleted("old"));
        Assert.True(a.Knows("new"));
        Assert.Equal(2, _cache.Usage.Entries);
        CloseAndAssertEmpty();
    }

    [Fact]
    public void DiscardHistoryRetainsKnownIdAndIndependentLeaseSurvivesClose()
    {
        using var scope = _cache.Open(Guid.NewGuid());
        Remember(scope, "discarded", RequestValue("retained request"), "retained response");
        using var history = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("discarded"));
        scope.DiscardHistory("discarded");
        Assert.True(scope.Knows("discarded"));
        Assert.False(scope.IsCompleted("discarded"));
        Assert.Null(scope.AcquireHistory("discarded"));
        Assert.Equal(1, _cache.Usage.Entries);
        scope.Dispose();
        _cache.Dispose();
        _spool.Dispose();
        Assert.Equal(0, _cache.Usage.Entries);
        Assert.True(_store.Usage.PhysicalBytes > 0);
        Assert.Equal(new[] { "retained request" }, InputTexts(history.MaterializeRequest()));
        Assert.Equal("retained response", history.MaterializeResponse()["value"]);
        history.Dispose();
        CloseAndAssertEmpty();
    }

    [Fact]
    public void CommitAfterConnectionCloseFailsAndReleasesPreparedCredits()
    {
        using var scope = _cache.Open(Guid.NewGuid());
        var response = ResponseValue("closed", "not committed");
        using var prepared = scope.Prepare(RequestValue(), null, generate: false, cancellationToken: default);
        prepared.ReserveKnownResponse(response, default);
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Commit(response, default));
        Assert.False(prepared.Committed);
        Assert.False(scope.Knows("closed"));
        Assert.Equal(0, _cache.Usage.Entries);
        prepared.Dispose();
        CloseAndAssertEmpty();
    }

    [Fact]
    public async Task ConcurrentCloseAndIndependentHistoryReadsReleaseAllOwners()
    {
        await Task.WhenAll(Enumerable.Range(0, Settings.CacheConcurrentWorkers).Select(async worker =>
        {
            using var scope = _cache.Open(Guid.NewGuid());
            Remember(scope, "same", RequestValue("worker" + worker), "worker" + worker);
            using var history = Assert.IsType<ClientHistoryVersion>(scope.AcquireHistory("same"));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = Task.Run(async () =>
            {
                await start.Task;
                Assert.Equal(new[] { "worker" + worker }, InputTexts(history.MaterializeRequest()));
                Assert.Equal("worker" + worker, history.MaterializeResponse()["value"]);
            });
            var close = Task.Run(async () =>
            {
                await start.Task;
                scope.Dispose();
            });
            start.TrySetResult();
            await Task.WhenAll(read, close);
            Assert.False(scope.Knows("same"));
            Assert.Throws<ObjectDisposedException>(() => scope.AcquireHistory("same"));
        }));
        CloseAndAssertEmpty();
    }

    [Fact]
    public async Task OlderWsHistorySurvivesWarmupChurnWithoutCallingModelAndCloseClearsQuota()
    {
        using (var context = new MultiAgentApiTestContext(ctx => Stream(ctx, Events(Terminal(Message("unexpected")))), connectionCache: _cache))
        {
            context.CatalogEnabled = false;
            using var socket = context.Socket();
            var operation = context.Service.ResponsesWebSocket();
            try
            {
                D Warm() => new() { ["type"] = "response.create", ["model"] = "fake", ["store"] = false, ["generate"] = false, ["input"] = "hello" };
                socket.ClientSend(Warm());
                var first = Assert.IsType<D>((await socket.Until("response.completed", context.Lifetime.Token))["response"]);
                for (var index = 1; index < Settings.CacheWsWarmupVersions; index++)
                {
                    socket.ClientSend(Warm());
                    await socket.Until("response.completed", context.Lifetime.Token);
                }
                socket.ClientSend(new() { ["type"] = "response.create", ["model"] = "fake", ["store"] = false, ["generate"] = false, ["previous_response_id"] = first["id"], ["input"] = "next" });
                var continued = Assert.IsType<D>((await socket.Until("response.completed", context.Lifetime.Token))["response"]);
                Assert.NotEqual(first["id"], continued["id"]);
                Assert.Equal(Settings.CacheWsWarmupVersions + 1, _cache.Usage.Entries);
                Assert.Empty(context.FakeEndpoint.Calls);
                Assert.DoesNotContain(socket.Sent, item => Type(item) is "error" or "response.failed");
            }
            finally { socket.ClientClose(); await operation; }
            Assert.Equal(0, _cache.Usage.Entries);
        }
        CloseAndAssertEmpty();
    }

    [Fact]
    public async Task WsFailureAfterTerminalAttemptDoesNotSendSecondErrorTerminal()
    {
        using (var context = new MultiAgentApiTestContext(async ctx =>
        {
            await Stream(ctx, Events(new D { ["type"] = "response.failed", ["response"] = new D { ["id"] = "failed-once", ["status"] = "failed", ["output"] = new List<object?>() } }));
            throw new OpenCodex.Core.Errors.UpstreamException("failure after terminal send");
        }, connectionCache: _cache))
        {
            context.CatalogEnabled = false;
            using var socket = context.Socket();
            var operation = context.Service.ResponsesWebSocket();
            try
            {
                var request = Request();
                request["type"] = "response.create";
                socket.ClientSend(request);
                await socket.Until("response.failed", context.Lifetime.Token);
            }
            finally { socket.ClientClose(); await operation; }
            Assert.Single(socket.Sent, item => Type(item) == "response.failed");
            Assert.DoesNotContain(socket.Sent, item => Type(item) == "error" || Type(item) == "response.completed");
        }
        CloseAndAssertEmpty();
    }

    private static D RequestValue(string value = "x") => new() { ["model"] = "fake", ["input"] = value };
    private static D ResponseValue(string id, string value) => new() { ["id"] = id, ["status"] = "completed", ["output"] = new List<object?>(), ["value"] = value };
    private static string[] InputTexts(D request) => JsonDictionaryValue.List(request, "input").OfType<D>().Select(item => JsonDictionaryValue.String(item, "content")).ToArray();

    private static D Remember(ClientConnectionCache.Connection scope, string id, D request, string value = "x", ClientHistoryVersion? previous = null)
    {
        using var prepared = scope.Prepare(request, previous, generate: false, cancellationToken: default);
        var response = ResponseValue(id, value);
        prepared.ReserveKnownResponse(response, default);
        prepared.Commit(response, default);
        Assert.True(prepared.Committed);
        return response;
    }

    private long UsedCredits()
    {
        var usage = _store.Usage;
        return checked(usage.PhysicalBytes + usage.MetadataBytes);
    }

    private void CloseAndAssertEmpty()
    {
        _cache.Dispose();
        _spool.Dispose();
        Assert.Equal((0, 0L), _cache.Usage);
        Assert.Equal((0L, 0L, 0, 0), _store.Usage);
    }

    private sealed record CacheTestParameters(int HistoricalRequestBytes, int CacheHistoryVersions, int CacheConcurrentWorkers, int CacheWsWarmupVersions)
    {
        public static CacheTestParameters Load() => new(
            WsHistoryTestParameters.HistoricalRequestBytes,
            WsHistoryTestParameters.CacheHistoryVersions,
            WsHistoryTestParameters.CacheConcurrentWorkers,
            WsHistoryTestParameters.CacheWsWarmupVersions);
    }
}
