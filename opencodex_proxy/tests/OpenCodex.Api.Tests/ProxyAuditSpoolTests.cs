using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyAuditSpoolTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ocx-spool-tests-"+Guid.NewGuid().ToString("N"));
    private ProxyAuditSpoolOptions Options(long bytes=8*1024*1024,int files=8) => new() { Directory=_directory,MemoryThresholdBytes=0,MaxBodyBytes=bytes,MaxTotalBytes=bytes*2,MaxFiles=files,OrphanAgeSeconds=1 };
    [Fact]
    public void SegmentedStringEscapingAndSurrogateBoundariesAreByteIdenticalToExistingJsonContract()
    {
        var spool=new ProxyAuditSpool(Options());
        var text=new string('x',4095)+"😀\r\n\"\\中"+new string('y',8191)+"😀end";
        var value=new Dictionary<string,object?> { ["text"]=text,["nested"]=new object?[] { null,true,12,"" } };
        using var old=ProxyLogBodyHandle.CaptureJson(value);using var body=spool.CaptureJson(value);
        using var source=body.ReadSnapshot().Spool!.OpenRead();using var bytes=new MemoryStream();source.CopyTo(bytes);
        Assert.Equal(old.ReadSnapshot().Utf8!.Value.ToArray(),bytes.ToArray());
    }
    [Fact]
    public void SmallSnapshotsStayInMemoryAndSpillOnlyAfterConfiguredThreshold()
    {
        var options=Options();options.MemoryThresholdBytes=256;
        var spool=new ProxyAuditSpool(options);
        using(var small=spool.CaptureJson(new { text="small" }))
        {
            Assert.Empty(Directory.EnumerateFiles(_directory));
            using var read=small.ReadSnapshot().Spool!.OpenRead();
            Assert.Contains("small",new StreamReader(read).ReadToEnd());
            Assert.True(spool.Usage.Bytes > 0);
        }
        using(var large=spool.CaptureJson(new { text=new string('x',1000) })) Assert.Single(Directory.EnumerateFiles(_directory));
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_directory));
    }
    [Fact]
    public void CompleteJsonIsImmutableAndRoundTripsThroughExistingManifestWithoutTrackedBlockData()
    {
        var spool=new ProxyAuditSpool(Options());
        var source=new Dictionary<string,object?> { ["text"]="中文😀"+new string('x',300000),["usage"]=new { input_tokens=7,output_tokens=11 } };
        using var expected=ProxyLogBodyHandle.CaptureJson(source);
        var exact=expected.ReadSnapshot().Utf8!.Value.ToArray();
        using var body=spool.CaptureJson(source);source.Clear();
        var snapshot=body.ReadSnapshot();Assert.Null(snapshot.Utf8);Assert.NotNull(snapshot.Spool);
        using(var read=snapshot.Spool!.OpenRead()) { using var bytes=new MemoryStream();read.CopyTo(bytes);Assert.Equal(exact,bytes.ToArray()); }
        using var db=Database();var log=Log(db);var second=Log(db);db.SaveChanges();var store=new LogContentStore(db);
        var file=snapshot.Spool.PathForTests;
        store.WriteBodies(log.Id,[RequestLogContentSlot.ResponseBody],_=>body);
        Assert.False(File.Exists(file));Assert.Equal((0L,0),spool.Usage);
        Assert.InRange(store.PeakStreamBatchRawBytes,1,16*32768);
        store.WriteBodies(second.Id,[RequestLogContentSlot.ResponseBody],_=>body);
        Assert.Equal(Encoding.UTF8.GetString(exact),store.Read(second.Id).Get(RequestLogContentSlot.ResponseBody));
        Assert.Single(db.LogContentManifests);Assert.Empty(db.ChangeTracker.Entries<LogContentBlock>());
        using var parsed=JsonDocument.Parse(store.Read(log.Id).Get(RequestLogContentSlot.ResponseBody)!);
        Assert.Equal(11,parsed.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }
    [Fact]
    public void TransactionFailureRetainsSpoolForRetryAndDoesNotLeavePartialBlocksOrReferences()
    {
        var spool=new ProxyAuditSpool(Options());using var body=spool.CaptureJson(new { text=new string('a',120000) });
        using var db=Database();var log=Log(db);db.SaveChanges();var store=new LogContentStore(db);
        Assert.Throws<IOException>(()=>store.WriteBodies(log.Id,[RequestLogContentSlot.ResponseBody,RequestLogContentSlot.WebSearchJson],
            slot=>slot==RequestLogContentSlot.ResponseBody?body:throw new IOException("injected rollback")));
        Assert.NotNull(body.ReadSnapshot().Spool);Assert.Empty(db.RequestLogContentRefs);Assert.Empty(db.LogContentBlocks);
        store.WriteBodies(log.Id,[RequestLogContentSlot.ResponseBody],_=>body);
        Assert.Equal((0L,0),spool.Usage);Assert.Contains(new string('a',120000),store.Read(log.Id).Get(RequestLogContentSlot.ResponseBody));
    }
    [Fact]
    public void ByteFileAndCancellationFailuresAreExplicitAndReleaseAllOwnedResources()
    {
        var spool=new ProxyAuditSpool(Options(100,1));
        Assert.Throws<IOException>(()=>spool.CaptureJson(new { text=new string('x',1000) }));
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_directory));
        using(var one=spool.CaptureJson(new { text="one" })) Assert.Throws<IOException>(()=>spool.CaptureJson(new { text="two" }));
        using var cancel=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>spool.CaptureJson(CancelDuringEnumeration(),cancel.Token));
        Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_directory));
        IEnumerable<int> CancelDuringEnumeration() { cancel.Cancel();yield return 1; }
    }
    [Fact]
    public void GlobalByteQuotaCountsConcurrentBodiesAndReturnsCapacityAfterDisposal()
    {
        var options=Options();options.MaxTotalBytes=20;
        var spool=new ProxyAuditSpool(options);
        using(var first=spool.CaptureJson("1234567890")) Assert.Throws<IOException>(()=>spool.CaptureJson("1234567890"));
        using var next=spool.CaptureJson("1234567890");Assert.Equal(12,spool.Usage.Bytes);
    }
    [Fact]
    public void DiskWriteFailureCleansFileAndQuotaRatherThanClaimingCompleteAudit()
    {
        var spool=new ProxyAuditSpool(Options()) { CreateFile=(path,options)=>new FailingWrite(new FileStream(path,options)) };
        var error=Assert.Throws<IOException>(()=>spool.CaptureJson(new { text="body" }));
        Assert.Contains("disk full",error.Message);Assert.Equal((0L,0),spool.Usage);Assert.Empty(Directory.EnumerateFiles(_directory));
    }
    [Fact]
    public void CleanupDeletesOldOrphansButNotLiveSnapshotsFreshOrUnrelatedFiles()
    {
        var spool=new ProxyAuditSpool(Options());using var live=spool.CaptureJson(new { text="live" });
        var active=live.ReadSnapshot().Spool!.PathForTests;
        File.SetLastWriteTimeUtc(active,DateTime.UtcNow.AddHours(-1));
        var orphan=Path.Combine(_directory,$"audit-{Guid.NewGuid():N}.tmp");File.WriteAllText(orphan,"orphan");File.SetLastWriteTimeUtc(orphan,DateTime.UtcNow.AddHours(-1));
        var fresh=Path.Combine(_directory,$"audit-{Guid.NewGuid():N}.tmp");File.WriteAllText(fresh,"fresh");
        var unrelated=Path.Combine(_directory,"audit-not-managed.tmp");File.WriteAllText(unrelated,"keep");File.SetLastWriteTimeUtc(unrelated,DateTime.UtcNow.AddHours(-1));
        Assert.Equal(1,spool.CleanupExpired());Assert.False(File.Exists(orphan));Assert.True(File.Exists(active));Assert.True(File.Exists(fresh));Assert.True(File.Exists(unrelated));
    }
    [Fact]
    public void FreshCrashLeftoversCountTowardsAdmissionQuotaAfterRestart()
    {
        Directory.CreateDirectory(_directory);
        var path=Path.Combine(_directory,$"audit-{Guid.NewGuid():N}.tmp");File.WriteAllText(path,new string('x',200));
        var options=Options(100);options.MaxTotalBytes=100;
        var spool=new ProxyAuditSpool(options);
        Assert.Throws<IOException>(()=>spool.CaptureJson("new"));
        Assert.Equal((200L,1),spool.Usage);
        File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddHours(-1));spool.CleanupExpired();
        using var allowed=spool.CaptureJson("new");Assert.Equal((5L,1),spool.Usage);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2048)] [InlineData(32768)] [InlineData(200000)]
    public void StreamCdcHasIdenticalHashesBoundariesAndShortReadBehavior(int size)
    {
        var bytes=new byte[size];new Random(123).NextBytes(bytes);var memory=LogContentCodec.Plan(bytes);
        using var source=new ShortReads(bytes);var streamed=LogContentCodec.Plan(source,memory.Hash,size);
        Assert.Equal(memory.Chunks.Select(c=>(c.Offset,c.Length,c.Hash)),streamed.Chunks.Select(c=>(c.Offset,c.Length,c.Hash)));
        Assert.Empty(streamed.Source.ToArray());Assert.Equal(memory.Hash,streamed.Hash);
    }
    private sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    { public override int Read(byte[] buffer,int offset,int count)=>base.Read(buffer,offset,Math.Min(count,17)); }
    private sealed class FailingWrite(Stream inner) : Stream
    {
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>0;public override long Position { get=>0;set=>throw new NotSupportedException(); }
        public override void Write(byte[] buffer,int offset,int count)=>throw new IOException("injected disk full");
        public override void Write(ReadOnlySpan<byte> buffer)=>throw new IOException("injected disk full");
        public override void Flush()=>inner.Flush();public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) { inner.Dispose();base.Dispose(disposing); }
    }
    private static OpenCodexSqliteDbContext Database() { var db=OpenCodexDbContextFactory.CreateSqlite("Data Source=:memory:");db.Database.OpenConnection();db.Database.Migrate();return db; }
    private static RequestLog Log(OpenCodexSqliteDbContext db) { var log=new RequestLog { Id=Guid.NewGuid(),RequestId=Guid.NewGuid().ToString(),OwnerUserId=Guid.Empty };db.RequestLogs.Add(log);return log; }
    public void Dispose() { if(Directory.Exists(_directory)) Directory.Delete(_directory,true); }
}
