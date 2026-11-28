using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace OpenCodex.Api.Tests;

public sealed class SnapshotStoreRegressionTests
{
    private static readonly TestParameters Settings = TestParameters.Load();
    private readonly ITestOutputHelper _output;

    public SnapshotStoreRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReservationCaptureIsNotRejectedByLogicalCapacity()
    {
        using var fixture = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        using var reservation = fixture.Store.Reserve(fixture.Token);
        var lease = reservation.CaptureUtf8(payload, fixture.Token);
        using var audit = reservation.AuditCapture.CreateStaging(fixture.Token);
        audit.Write(payload);

        using (var read = lease.Read()) Assert.Equal(payload, ReadAll(read));
        using (var read = audit.OpenSnapshotRead()) Assert.Equal(payload, ReadAll(read));
        Assert.Equal(2, fixture.Store.Usage.Containers);
        audit.Dispose();
        lease.Dispose();
        reservation.Dispose();
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void IndependentReadersSurviveReservationLogCommitAndSpoolDisposalUntilLastReference()
    {
        using var fixture = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.Store.Reserve(fixture.Token);
        var lease = fixture.Hold(reservation.CaptureUtf8(payload, fixture.Token));
        var charged = fixture.Store.Usage;
        var historyOwner = fixture.Hold(lease.AddRef());
        var logOwner = fixture.Hold(lease.ToLogHandle());
        var path = logOwner.ReadSnapshot().Spool!.PathForTests;
        var ownerPath = Path.Combine(Path.GetDirectoryName(path)!, "owner.lock");
        var historyReader = fixture.Hold(historyOwner.Read());
        var logReader = fixture.Hold(logOwner.ReadSnapshot().Spool!.OpenRead());
        Assert.Equal(charged, fixture.Store.Usage);

        lease.Dispose();
        historyOwner.Dispose();
        reservation.Dispose();
        Assert.Equal(payload, ReadAll(historyReader));
        historyReader.Position = 0;
        logOwner.ReleaseCommittedBuffer();
        Assert.Null(logOwner.ReadSnapshot().Spool);
        Assert.Equal(payload, ReadAll(logReader));
        logReader.Position = 0;
        fixture.Spool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Store.Reserve());
        Assert.True(File.Exists(path));
        AssertOwnerLockHeld(ownerPath);
        Assert.Equal(payload, ReadAll(historyReader));
        Assert.Equal(payload, ReadAll(logReader));
        historyReader.Dispose();
        Assert.True(File.Exists(path));
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        AssertOwnerLockHeld(ownerPath);
        logReader.Dispose();
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(ownerPath));
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void SameBaseSpoolCleanupCannotDeleteAgedActiveContainerAfterWriterCloses()
    {
        using var owner = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = owner.Store.Reserve(owner.Token);
        var lease = owner.Hold(reservation.CaptureUtf8(payload, owner.Token));
        var path = lease.PathForTests;
        var snapshotDirectory = Path.GetDirectoryName(path)!;
        var ownerPath = Path.Combine(snapshotDirectory, "owner.lock");
        reservation.Dispose();
        using (var closedWriterProbe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(closedWriterProbe.Length >= payload.LongLength);
        var fixtureUtc = owner.FixtureUtc;
        var agedUtc = fixtureUtc.AddSeconds(-checked(2L * Settings.OrphanAgeSeconds));
        File.SetLastWriteTimeUtc(path, agedUtc);
        Directory.SetLastWriteTimeUtc(snapshotDirectory, agedUtc);

        using var sweeper = new Fixture(Settings, baseDirectory: owner.DirectoryPath, ownsDirectory: false);
        Assert.True(File.Exists(path));
        var orphan = Path.Combine(owner.DirectoryPath, $"audit-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(orphan, payload);
        File.SetLastWriteTimeUtc(orphan, agedUtc);
        Assert.Equal(1, sweeper.Spool.CleanupExpired());
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(path));
        AssertOwnerLockHeld(ownerPath);
        using (var read = lease.Read()) Assert.Equal(payload, ReadAll(read));

        owner.Spool.Dispose();
        var retainedReader = owner.Hold(lease.Read());
        lease.Dispose();
        sweeper.Spool.CleanupExpired();
        Assert.True(File.Exists(path));
        AssertOwnerLockHeld(ownerPath);
        Assert.Equal(payload, ReadAll(retainedReader));
        retainedReader.Dispose();
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(snapshotDirectory));
        Assert.Equal((0L, 0L, 0, 0), owner.Store.Usage);
        sweeper.Spool.CleanupExpired();
        sweeper.CloseAndAssertEmpty();
        owner.CloseAndAssertEmpty();
    }

    [Fact]
    [Trait("SnapshotScale", "RootCaptureFields")]
    public void RootTextAndBinaryFieldCountsShareOneAuditContainerWithExactRawUtf16()
    {
        using var fixture = new Fixture(Settings);
        var timer = Stopwatch.StartNew();
        var fields = ProtocolConversionParameters.Current.SseMaxCaptureFields;
        var logicalBuffers = checked(2 * fields);
        const string rawSuffix = "\uD83D\uDE00\0\"\\\r\n中\uD83D";
        var textValues = Enumerable.Range(0, fields).Select(index => "t" + index.ToString(CultureInfo.InvariantCulture) + rawSuffix).ToArray();
        var binaryValues = Enumerable.Range(0, fields).Select(index => "b" + index.ToString(CultureInfo.InvariantCulture) + rawSuffix).ToArray();
        var physicalPayload = checked(sizeof(char) * (textValues.Sum(value => (long)value.Length) + binaryValues.Sum(value => (long)value.Length)));
        try
        {
            Assert.InRange(fields, 1, int.MaxValue / 2);
            var reservation = fixture.ReservePayload();
            Assert.Equal(0, reservation.AuditCapture.MemoryThresholdBytes);
            var textBuffers = new IProtocolAuditBuffer[fields];
            var binaryBuffers = new IProtocolAuditBuffer[fields];
            for (var index = 0; index < fields; index++)
            {
                fixture.Token.ThrowIfCancellationRequested();
                textBuffers[index] = fixture.Hold(reservation.AuditCapture.CreateStaging(fixture.Token));
                binaryBuffers[index] = fixture.Hold(reservation.AuditCapture.CreateStaging(fixture.Token));
                WriteSplitRawUtf16(textBuffers[index], textValues[index]);
                WriteSplitRawUtf16(binaryBuffers[index], binaryValues[index]);
            }
            var usage = fixture.Store.Usage;
            Assert.Equal(1, usage.Containers);
            Assert.True(usage.MetadataBytes >= checked((long)logicalBuffers * SnapshotStore.BufferMetadataBytes));
            Assert.True(usage.PhysicalBytes >= physicalPayload);
            var container = Assert.Single(Directory.EnumerateFiles(fixture.DirectoryPath, "audit-*.tmp", SearchOption.AllDirectories));
            var ownerPath = Path.Combine(Path.GetDirectoryName(container)!, "owner.lock");
            Assert.Equal(checked(new FileInfo(container).Length + new FileInfo(ownerPath).Length), usage.PhysicalBytes);
            Assert.Equal(2, Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Count());
            for (var index = 0; index < fields; index++)
            {
                fixture.Token.ThrowIfCancellationRequested();
                using (var read = textBuffers[index].OpenSnapshotRead())
                    Assert.Equal(MemoryMarshal.AsBytes(textValues[index].AsSpan()).ToArray(), ReadAll(read));
                using (var read = binaryBuffers[index].OpenSnapshotRead())
                    Assert.Equal(MemoryMarshal.AsBytes(binaryValues[index].AsSpan()).ToArray(), ReadAll(read));
            }
            fixture.CloseAndAssertEmpty();
        }
        finally
        {
            timer.Stop();
            _output.WriteLine("Root SseMaxCaptureFields={0}; text+binary buffers={1}; payload bytes={2}; elapsed milliseconds={3}; unchanged case timeout milliseconds={4}.",
                fields, logicalBuffers, physicalPayload, timer.ElapsedMilliseconds, Settings.CaseTimeoutMilliseconds);
        }
    }

    [Fact]
    public void RawUtf16PrefixReaderProtectsPageChainAcrossSurrogateBoundaryAndBufferReuse()
    {
        using var fixture = new Fixture(Settings);
        Assert.True(fixture.Store.PageBytes >= sizeof(char));
        var firstHalf = new string('x', fixture.Store.PageBytes / sizeof(char) - 1) + "\uD83D";
        const string secondHalf = "\uDE00\0\"\\\r\n中\uD83D";
        var replacement = new string('z', firstHalf.Length + secondHalf.Length);
        var original = firstHalf + secondHalf;
        var reservation = fixture.ReservePayload();
        var originalBuffer = fixture.Hold(reservation.AuditCapture.CreateStaging(fixture.Token));
        originalBuffer.Write(MemoryMarshal.AsBytes(firstHalf.AsSpan()));
        var prefixReader = fixture.Hold(originalBuffer.OpenSnapshotRead());
        originalBuffer.Write(MemoryMarshal.AsBytes(secondHalf.AsSpan()));
        var completeReader = fixture.Hold(originalBuffer.OpenSnapshotRead());
        originalBuffer.Dispose();
        var replacementBuffer = fixture.Hold(reservation.AuditCapture.CreateStaging(fixture.Token));
        replacementBuffer.Write(MemoryMarshal.AsBytes(replacement.AsSpan()));
        using (var read = replacementBuffer.OpenSnapshotRead())
            Assert.Equal(MemoryMarshal.AsBytes(replacement.AsSpan()).ToArray(), ReadAll(read));
        replacementBuffer.Dispose();
        reservation.Dispose();
        fixture.Spool.Dispose();
        Assert.Equal(MemoryMarshal.AsBytes(firstHalf.AsSpan()).ToArray(), ReadAll(prefixReader));
        Assert.Equal(MemoryMarshal.AsBytes(original.AsSpan()).ToArray(), ReadAll(completeReader));
        prefixReader.Dispose();
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        completeReader.Dispose();
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void CreateFileIOExceptionKeepsUndeletableContainerChargedAndPublicMessageSafe()
    {
        string? attemptedPath = null;
        IOException? injected = null;
        using var fixture = new Fixture(Settings, createFile: (path, _) =>
        {
            attemptedPath = path;
            Directory.CreateDirectory(path);
            injected = new IOException("injected private create failure at " + path);
            throw injected;
        });
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload();
        var error = Assert.Throws<SnapshotPersistenceException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.Same(injected, error.InnerException);
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.DoesNotContain("injected private", error.Message);
        Assert.NotNull(attemptedPath);
        Assert.True(Directory.Exists(attemptedPath));
        Assert.Equal(1, fixture.Store.Usage.Containers);
        reservation.Dispose();
        var pending = fixture.Store.Usage;
        Assert.True(pending.MetadataBytes > 0);
        Assert.Equal(0, pending.Containers);
        fixture.Spool.Dispose();
        Assert.Equal(pending, fixture.Store.Usage);
        fixture.Spool.CleanupExpired();
        Assert.True(Directory.Exists(attemptedPath));
        Assert.Equal(pending, fixture.Store.Usage);

        var verifiedPath = Path.GetFullPath(attemptedPath!);
        Assert.StartsWith(Path.GetFullPath(fixture.DirectoryPath) + Path.DirectorySeparatorChar, verifiedPath);
        Directory.Delete(verifiedPath, recursive: false);
        fixture.Spool.CleanupExpired();
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void PartialAllocationIOExceptionRetainsPhysicalChargeUntilActualContainerDeletion()
    {
        string? attemptedPath = null;
        long offeredLength = 0;
        using var fixture = new Fixture(Settings, createFile: (path, options) =>
        {
            attemptedPath = path;
            return new FailAfterGrowth(new FileStream(path, options), length => offeredLength = length,
                "injected private partial allocation at " + path);
        });
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload();
        var error = Assert.Throws<SnapshotPersistenceException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.Contains("injected private partial allocation", Assert.IsType<IOException>(error.InnerException).Message);
        Assert.NotNull(attemptedPath);
        Assert.True(File.Exists(attemptedPath));
        Assert.True(offeredLength > 0);
        Assert.Equal(offeredLength, new FileInfo(attemptedPath!).Length);
        var ownerPath = Path.Combine(Path.GetDirectoryName(attemptedPath!)!, "owner.lock");
        Assert.Equal(checked(offeredLength + new FileInfo(ownerPath).Length), fixture.Store.Usage.PhysicalBytes);
        var retained = fixture.Store.Usage;
        fixture.Spool.Dispose();
        Assert.Equal(retained, fixture.Store.Usage);
        Assert.True(File.Exists(attemptedPath));
        AssertOwnerLockHeld(ownerPath);
        reservation.Dispose();
        Assert.False(File.Exists(attemptedPath));
        Assert.False(File.Exists(ownerPath));
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void CancellationAndDisposedReservationRejectCaptureWithoutReleasingLiveReaders()
    {
        using var fixture = new Fixture(Settings);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload();
        var lease = fixture.Hold(reservation.CaptureUtf8(payload, cancel.Token));
        var raw = fixture.Hold(reservation.AuditCapture.CreateStaging(cancel.Token));
        raw.Write(payload);
        var historyReader = fixture.Hold(lease.Read());
        var rawReader = fixture.Hold(raw.OpenSnapshotRead());
        var before = fixture.Store.Usage;
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Store.Reserve(cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reservation.CaptureUtf8(payload, cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reservation.AuditCapture.CreateStaging(cancel.Token));
        reservation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.Throws<ObjectDisposedException>(() => reservation.AuditCapture.CreateStaging(fixture.Token));
        raw.Dispose();
        lease.Dispose();
        fixture.Spool.Dispose();
        Assert.Equal(payload, ReadAll(historyReader));
        Assert.Equal(payload, ReadAll(rawReader));
        historyReader.Dispose();
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        rawReader.Dispose();
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public async Task ConcurrentReservationsAndDisposalRemainConsistentWithoutCapacityAdmission()
    {
        using var fixture = new Fixture(Settings);
        Assert.True(Settings.ConcurrentWorkers > 1);
        var accepted = new SnapshotReservation?[Settings.ConcurrentWorkers];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, Settings.ConcurrentWorkers).Select(worker => Task.Run(async () =>
        {
            await start.Task.WaitAsync(fixture.Token);
            accepted[worker] = fixture.Hold(fixture.Store.Reserve(fixture.Token));
            fixture.AssertLedgerWithinRoot();
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts).WaitAsync(fixture.Token);
        Assert.All(accepted, Assert.NotNull);
        Assert.Equal(Settings.ConcurrentWorkers, accepted.Count(reservation => reservation is not null));
        Assert.Equal(0L, fixture.Store.Usage.PhysicalBytes);
        Assert.Equal(0, fixture.Store.Usage.Containers);

        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = accepted.Select(reservation => Task.Run(async () =>
        {
            await releaseStart.Task.WaitAsync(fixture.Token);
            reservation!.Dispose();
            fixture.AssertLedgerWithinRoot();
            using var next = fixture.Store.Reserve(fixture.Token);
            fixture.AssertLedgerWithinRoot();
        })).ToArray();
        releaseStart.SetResult();
        await Task.WhenAll(releases).WaitAsync(fixture.Token);
        fixture.Spool.Dispose();
        Assert.Equal((0L, 0L, 0, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    private sealed class FailAfterGrowth(Stream inner, Action<long> grown, string message) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void SetLength(long value)
        {
            inner.SetLength(value);
            grown(value);
            throw new IOException(message);
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void WriteSplitRawUtf16(IProtocolAuditBuffer buffer, string value)
    {
        var split = value.IndexOf('\uD83D') + 1;
        Assert.InRange(split, 1, value.Length - 1);
        buffer.Write(MemoryMarshal.AsBytes(value.AsSpan(0, split)));
        buffer.Write(MemoryMarshal.AsBytes(value.AsSpan(split)));
    }

    private static byte[] Payload(int bytes)
    {
        var payload = new byte[bytes];
        Array.Fill(payload, (byte)'x');
        return payload;
    }

    private static byte[] ReadAll(Stream read)
    {
        var bytes = new byte[checked((int)read.Length)];
        read.ReadExactly(bytes);
        Assert.Equal(-1, read.ReadByte());
        return bytes;
    }

    private static void AssertOwnerLockHeld(string path)
    {
        Assert.True(File.Exists(path));
        Assert.ThrowsAny<IOException>(() =>
        {
            using var attempted = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _holders = [];
        private readonly bool _ownsDirectory;
        private readonly CancellationTokenSource _deadline;

        public Fixture(TestParameters parameters, string? baseDirectory = null, bool ownsDirectory = true,
            Func<string, FileStreamOptions, Stream>? createFile = null)
        {
            DirectoryPath = baseDirectory ?? Path.Combine(Path.GetTempPath(), "ocxp-snapshot-regression-" + Guid.NewGuid().ToString("N"));
            _ownsDirectory = ownsDirectory;
            _deadline = new CancellationTokenSource(parameters.CaseTimeoutMilliseconds);
            var options = new ProxyAuditSpoolOptions
            {
                Directory = DirectoryPath,
                MemoryThresholdBytes = 0,
                OrphanAgeSeconds = parameters.OrphanAgeSeconds
            };
            Spool = createFile is null ? new ProxyAuditSpool(options) : new ProxyAuditSpool(options) { CreateFile = createFile };
            Store = new SnapshotStore(Spool);
        }

        public DateTime FixtureUtc { get; } = DateTime.UtcNow;
        public string DirectoryPath { get; }
        public ProxyAuditSpool Spool { get; }
        public SnapshotStore Store { get; }
        public CancellationToken Token => _deadline.Token;

        public T Hold<T>(T holder) where T : IDisposable
        {
            lock (_holders) _holders.Add(holder);
            return holder;
        }

        public SnapshotReservation ReservePayload() => Hold(Store.Reserve(Token));

        public void AssertLedgerWithinRoot()
        {
            var usage = Store.Usage;
            Assert.InRange(usage.Files, 0, int.MaxValue);
            Assert.InRange(usage.Containers, 0, int.MaxValue);
            Assert.True(usage.PhysicalBytes >= 0);
            Assert.True(usage.MetadataBytes >= 0);
        }

        private void ReleaseHolders()
        {
            IDisposable[] holders;
            lock (_holders) { holders = _holders.ToArray(); _holders.Clear(); }
            for (var index = holders.Length - 1; index >= 0; index--) holders[index].Dispose();
        }

        public void CloseAndAssertEmpty()
        {
            ReleaseHolders();
            Spool.Dispose();
            Assert.Equal((0L, 0L, 0, 0), Store.Usage);
        }

        public void Dispose()
        {
            ReleaseHolders();
            Spool.Dispose();
            _deadline.Dispose();
            if (_ownsDirectory && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed record TestParameters(int SmallPayloadBytes, int CaseTimeoutMilliseconds, int ConcurrentWorkers, int OrphanAgeSeconds)
    {
        public static TestParameters Load() => new(
            WsHistoryTestParameters.SmallRequestBytes,
            WsHistoryTestParameters.CaseTimeoutMilliseconds,
            WsHistoryTestParameters.CacheConcurrentWorkers,
            WsHistoryTestParameters.SnapshotOrphanAgeSeconds);
    }
}
