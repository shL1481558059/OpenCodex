using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyLogBodyHandleTests
{
    [Fact]
    public void JsonSnapshot_DoesNotRetainMutableSourceOrReserializeAndHashIsComputedOnce()
    {
        var value = new CountingValue("original");
        var source = new Dictionary<string, object?> { ["nested"] = value };
        var handle = ProxyLogBodyHandle.CaptureJson(source);
        Assert.Equal(1, value.Reads);
        value.Text = "changed";
        source.Clear();
        var snapshots = Enumerable.Range(0, 32).AsParallel().Select(_ => handle.ReadSnapshot()).ToList();
        Assert.All(snapshots, snapshot => Assert.Contains("original", Encoding.UTF8.GetString(snapshot.Utf8!.Value.Span)));
        Assert.Single(snapshots.Select(snapshot => snapshot.Hash).Distinct());
        Assert.Equal(1, handle.HashComputations);
        Assert.Equal(1, value.Reads);
        Assert.NotEqual(handle.Version, ProxyLogBodyHandle.CaptureJson(source).Version);
    }

    [Fact]
    public void Utf8Snapshot_CopiesPublicCallerMemoryIncludingSlices()
    {
        var bytes = Encoding.UTF8.GetBytes("prefix正文suffix");
        var body = ProxyLogBodyHandle.CaptureUtf8(bytes.AsSpan(6, Encoding.UTF8.GetByteCount("正文")));
        Array.Fill(bytes, (byte)'x');
        Assert.Equal("正文", Encoding.UTF8.GetString(body.ReadSnapshot().Utf8!.Value.Span));
    }

    [Fact]
    public void Commit_ReleasesEncodedBytesAndReusesAddressAcrossLogsAndSlots()
    {
        using var db = Database();
        var first = Log(db, "first"); var second = Log(db, "second"); db.SaveChanges();
        var store = new LogContentStore(db);
        var body = ProxyLogBodyHandle.CaptureJson(new { text = new string('x', 100_000) });
        store.WriteBodies(first.Id, [RequestLogContentSlot.RequestBody], _ => body);
        var originalRef = db.RequestLogContentRefs.AsNoTracking().Single();
        Assert.Null(body.ReadSnapshot().Utf8);
        store.WriteBodies(first.Id, [RequestLogContentSlot.RequestBody], _ => body);
        store.WriteBodies(second.Id, [RequestLogContentSlot.RequestBody, RequestLogContentSlot.UpstreamRequestBody], _ => body);
        Assert.Equal(originalRef.Id, db.RequestLogContentRefs.AsNoTracking().Single(x => x.RequestLogId == first.Id).Id);
        Assert.Equal(1, body.HashComputations);
        Assert.Single(db.LogContentManifests);
        Assert.All(db.RequestLogContentRefs, reference => Assert.Equal(originalRef.ManifestId, reference.ManifestId));
        Assert.Contains(new string('x', 100_000), store.Read(second.Id).Get(RequestLogContentSlot.RequestBody));
        Assert.Empty(db.ChangeTracker.Entries<LogContentBlock>());
    }

    [Fact]
    public void RolledBackBatch_DoesNotReleaseSnapshotOrClaimPersistence()
    {
        using var db = Database(); var log = Log(db, "rollback"); db.SaveChanges();
        var store = new LogContentStore(db);
        var body = ProxyLogBodyHandle.CaptureJson(new { text = "retryable bytes" });
        Assert.Throws<IOException>(() => store.WriteBodies(log.Id,
            [RequestLogContentSlot.RequestBody, RequestLogContentSlot.ResponseBody], slot =>
                slot == RequestLogContentSlot.RequestBody ? body : throw new IOException("injected failure")));
        Assert.NotNull(body.ReadSnapshot().Utf8);
        Assert.Empty(db.RequestLogContentRefs);
        store.WriteBodies(log.Id, [RequestLogContentSlot.RequestBody], _ => body);
        Assert.Null(body.ReadSnapshot().Utf8);
        Assert.Equal(1, body.HashComputations);
        Assert.Contains("retryable bytes", store.Read(log.Id).Get(RequestLogContentSlot.RequestBody));
    }

    [Fact]
    public void MissingCommittedManifest_FailsExplicitlyRatherThanWritingNullOrChangedSource()
    {
        using var db = Database(); var first = Log(db, "first"); var second = Log(db, "second"); db.SaveChanges();
        var store = new LogContentStore(db);
        var body = ProxyLogBodyHandle.CaptureJson(new { text = "must remain auditable" });
        store.WriteBodies(first.Id, [RequestLogContentSlot.RequestBody], _ => body);
        store.Write(first.Id, new Dictionary<RequestLogContentSlot, string?> { [RequestLogContentSlot.RequestBody] = null });
        Assert.Empty(db.LogContentManifests);
        var error = Assert.Throws<InvalidDataException>(() => store.WriteBodies(second.Id,
            [RequestLogContentSlot.RequestBody], _ => body));
        Assert.Contains("audit", error.Message);
        Assert.Empty(db.RequestLogContentRefs);
    }

    [Fact]
    public void IndependentVersions_DoNotCrossContaminateIdenticalRequestScopes()
    {
        using var db = Database(); var first = Log(db, "owner-one"); var second = Log(db, "owner-two"); db.SaveChanges();
        var source = new Dictionary<string, object?> { ["text"] = "one" };
        var one = ProxyLogBodyHandle.CaptureJson(source);
        source["text"] = "two";
        var two = ProxyLogBodyHandle.CaptureJson(source);
        var store = new LogContentStore(db);
        store.WriteBodies(first.Id, [RequestLogContentSlot.RequestBody], _ => one);
        store.WriteBodies(second.Id, [RequestLogContentSlot.RequestBody], _ => two);
        Assert.Contains("one", store.Read(first.Id).Get(RequestLogContentSlot.RequestBody));
        Assert.Contains("two", store.Read(second.Id).Get(RequestLogContentSlot.RequestBody));
        Assert.NotEqual(one.Version, two.Version);
        Assert.NotEqual(one.ReadSnapshot().Hash, two.ReadSnapshot().Hash);
    }

    private static OpenCodexSqliteDbContext Database()
    {
        var db = OpenCodexDbContextFactory.CreateSqlite("Data Source=:memory:");
        db.Database.OpenConnection(); db.Database.Migrate(); return db;
    }

    private static RequestLog Log(OpenCodexSqliteDbContext db, string requestId)
    {
        var log = new RequestLog { Id = Guid.NewGuid(), RequestId = requestId, OwnerUserId = Guid.Empty };
        db.RequestLogs.Add(log); return log;
    }

    private sealed class CountingValue(string text)
    {
        [JsonIgnore] public string Text { get; set; } = text;
        [JsonIgnore] public int Reads { get; private set; }
        public string Value { get { Reads++; return Text; } }
    }
}
