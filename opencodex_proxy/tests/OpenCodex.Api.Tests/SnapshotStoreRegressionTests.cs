using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    public void ReservationCaptureCannotBorrowGlobalCreditsAndReportsSnapshotCapacity()
    {
        using var fixture = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload(payload.LongLength, maxLiveBuffers: 1, containers: 1);
        var lease = fixture.Hold(reservation.CaptureUtf8(payload, fixture.Token));
        var before = fixture.Store.Usage;
        var remaining = reservation.RemainingBytes;
        Assert.True(fixture.Store.MaxBytes - fixture.UsedCredits > payload.LongLength);

        var error = Assert.Throws<SnapshotCapacityExceededException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.True(error.RequestedBytes > error.AvailableBytes);
        Assert.InRange(error.AvailableBytes, 0L, remaining);
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.Equal(before, fixture.Store.Usage);
        Assert.Equal(remaining, reservation.RemainingBytes);

        var auditError = Assert.Throws<SnapshotCapacityExceededException>(() => reservation.AuditCapture.CreateStaging(fixture.Token));
        Assert.True(auditError.RequestedBytes > auditError.AvailableBytes);
        Assert.Equal(before, fixture.Store.Usage);
        using (var read = lease.Read()) Assert.Equal(payload, ReadAll(read));
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void PreReservedHistoryAndRawAuditStayAdmittedAtRootGlobalCapacity()
    {
        using var fixture = new Fixture(Settings);
        Assert.Equal(ProtocolConversionParameters.Current.SnapshotStoreMaxBytes, fixture.Store.MaxBytes);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload(checked(payload.LongLength * 3), maxLiveBuffers: 3, containers: 2);
        // Logical credits simulate a full root store; this never writes MaxBytes worth of data.
        var blocker = fixture.Hold(fixture.Store.Reserve(checked(fixture.Store.MaxBytes - fixture.UsedCredits), fixture.Token));
        Assert.Equal(fixture.Store.MaxBytes, fixture.UsedCredits);
        var full = fixture.Store.Usage;
        var minimumCredits = fixture.Store.EstimateCredits(0, 0, 0);
        var admissionError = Assert.Throws<SnapshotCapacityExceededException>(() => fixture.Store.Reserve(minimumCredits, fixture.Token));
        Assert.Equal(minimumCredits, admissionError.RequestedBytes);
        Assert.Equal(0L, admissionError.AvailableBytes);
        Assert.Equal(full, fixture.Store.Usage);

        var history = fixture.Hold(reservation.CaptureUtf8(payload, fixture.Token));
        var rawAudit = fixture.Hold(reservation.AuditCapture.CreateStaging(fixture.Token));
        rawAudit.Write(payload);
        var auditLog = fixture.Hold(reservation.CaptureAuditUtf8(payload, fixture.Token));
        Assert.Equal(fixture.Store.MaxBytes, fixture.UsedCredits);
        Assert.Equal(2, fixture.Store.Usage.Containers);
        using (var read = history.Read()) Assert.Equal(payload, ReadAll(read));
        using (var read = rawAudit.OpenSnapshotRead()) Assert.Equal(payload, ReadAll(read));
        using (var read = auditLog.ReadSnapshot().Spool!.OpenRead()) Assert.Equal(payload, ReadAll(read));
        blocker.Dispose();
        fixture.AssertLedgerWithinRoot();
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void IndependentReadersSurviveReservationLogCommitAndSpoolDisposalUntilLastReference()
    {
        using var fixture = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload(payload.LongLength, maxLiveBuffers: 1, containers: 1);
        var lease = fixture.Hold(reservation.CaptureUtf8(payload, fixture.Token));
        var charged = fixture.Store.Usage;
        var historyOwner = fixture.Hold(lease.AddRef());
        var logOwner = fixture.Hold(lease.ToLogHandle());
        var path = logOwner.ReadSnapshot().Spool!.PathForTests;
        var ownerPath = Path.Combine(Path.GetDirectoryName(path)!, "owner.lock");
        var historyReader = fixture.Hold(historyOwner.Read());
        var logReader = fixture.Hold(logOwner.ReadSnapshot().Spool!.OpenRead());
        Assert.Equal(charged, fixture.Store.Usage); // Additional owners must not charge the same blob again.

        lease.Dispose();
        historyOwner.Dispose();
        reservation.Dispose();
        Assert.Equal(payload, ReadAll(historyReader));
        historyReader.Position = 0;
        logOwner.ReleaseCommittedBuffer();
        Assert.Null(logOwner.ReadSnapshot().Spool);
        Assert.Equal(payload, ReadAll(logReader));
        logReader.Position = 0;
        fixture.Spool.Dispose(); // Stops new admission, not existing independent read ownership.
        Assert.Throws<ObjectDisposedException>(() => fixture.Store.Reserve(fixture.Store.EstimateCredits(0, 0, 0)));
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
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void SameBaseSpoolCleanupCannotDeleteAgedActiveContainerAfterWriterCloses()
    {
        using var owner = new Fixture(Settings);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = owner.ReservePayload(payload.LongLength, maxLiveBuffers: 1, containers: 1);
        var lease = owner.Hold(reservation.CaptureUtf8(payload, owner.Token));
        var path = lease.PathForTests;
        var snapshotDirectory = Path.GetDirectoryName(path)!;
        var ownerPath = Path.Combine(snapshotDirectory, "owner.lock");
        reservation.Dispose();
        // No reader is open yet. Exclusive payload access proves the writer FD has closed.
        using (var closedWriterProbe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(closedWriterProbe.Length >= payload.LongLength);
        var fixtureUtc = owner.FixtureUtc;
        var agedUtc = fixtureUtc.AddSeconds(-checked(2L * Settings.OrphanAgeSeconds));
        File.SetLastWriteTimeUtc(path, agedUtc);
        Directory.SetLastWriteTimeUtc(snapshotDirectory, agedUtc);

        // These are two real spool instances in one process sharing a base directory, not
        // a claim that this test has proved cross-process quota or locking behavior.
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
        Assert.Equal((0L, 0L, 0L, 0), owner.Store.Usage);
        sweeper.Spool.CleanupExpired(); // Refresh its inherited-file observations after actual deletion.
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
        var logicalBuffers = checked(2 * fields); // Text F and binary F are simultaneously live.
        const string rawSuffix = "\uD83D\uDE00\0\"\\\r\n中\uD83D";
        var textValues = Enumerable.Range(0, fields).Select(index => "t" + index.ToString(CultureInfo.InvariantCulture) + rawSuffix).ToArray();
        var binaryValues = Enumerable.Range(0, fields).Select(index => "b" + index.ToString(CultureInfo.InvariantCulture) + rawSuffix).ToArray();
        var physicalPayload = checked(sizeof(char) * (textValues.Sum(value => (long)value.Length) + binaryValues.Sum(value => (long)value.Length)));
        var credits = fixture.Store.EstimateCredits(physicalPayload, logicalBuffers, containers: 1);
        try
        {
            Assert.InRange(fields, 1, int.MaxValue / 2);
            Assert.True(credits > checked(physicalPayload + (long)logicalBuffers * SnapshotStore.BufferMetadataBytes));
            var reservation = fixture.ReservePayload(physicalPayload, logicalBuffers, containers: 1);
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
            Assert.Equal(credits, fixture.UsedCredits);
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
            fixture.AssertLedgerWithinRoot();
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
        var physicalPayload = checked(sizeof(char) * ((long)original.Length + replacement.Length));
        var reservation = fixture.ReservePayload(physicalPayload, maxLiveBuffers: 2, containers: 1);
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
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void CaptureWriteCapacityFailureDoesNotBorrowGlobalOrRefundAllocatedPages()
    {
        using var fixture = new Fixture(Settings);
        var payload = Payload(fixture.Store.PageBytes);
        var reservation = fixture.ReservePayload(payload.LongLength, maxLiveBuffers: 2, containers: 1);
        var original = fixture.Hold(reservation.CaptureUtf8(payload, fixture.Token));
        var oversized = Payload(checked(payload.Length + fixture.Store.PageBytes));
        var before = fixture.Store.Usage;
        var used = fixture.UsedCredits;
        var remaining = reservation.RemainingBytes;
        Assert.True(fixture.Store.MaxBytes - used > oversized.LongLength);
        var error = Assert.Throws<SnapshotCapacityExceededException>(() => reservation.CaptureUtf8(oversized, fixture.Token));
        Assert.True(error.RequestedBytes > error.AvailableBytes);
        Assert.Equal(0L, error.AvailableBytes);
        Assert.Equal(used, fixture.UsedCredits);
        Assert.True(fixture.Store.Usage.PhysicalBytes > before.PhysicalBytes);
        Assert.True(reservation.RemainingBytes < remaining);
        // A partially allocated failed capture is retained, not refunded around a still-live file.
        var path = original.PathForTests;
        using (var read = original.Read()) Assert.Equal(payload, ReadAll(read));
        fixture.Spool.Dispose();
        reservation.Dispose();
        Assert.True(File.Exists(path));
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        using (var read = original.Read()) Assert.Equal(payload, ReadAll(read));
        original.Dispose();
        Assert.False(File.Exists(path));
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
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
            // An empty directory at the offered file path makes File.Delete fail on every OS,
            // avoiding sleep, permission assumptions, or a fake deletion/refund hook.
            Directory.CreateDirectory(path);
            injected = new IOException("injected private create failure at " + path);
            throw injected;
        });
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload(payload.LongLength, maxLiveBuffers: 1, containers: 1);
        var credits = fixture.UsedCredits;
        var error = Assert.Throws<SnapshotPersistenceException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.Same(injected, error.InnerException);
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.DoesNotContain("injected private", error.Message);
        Assert.NotNull(attemptedPath);
        Assert.True(Directory.Exists(attemptedPath));
        Assert.Equal(credits, fixture.UsedCredits);
        Assert.Equal(1, fixture.Store.Usage.Containers);
        reservation.Dispose();
        var pending = fixture.Store.Usage;
        Assert.Equal(0L, pending.ReservedBytes);
        Assert.True(pending.MetadataBytes > 0);
        Assert.Equal(1, pending.Containers);
        fixture.Spool.Dispose();
        Assert.Equal(pending, fixture.Store.Usage);
        fixture.Spool.CleanupExpired();
        Assert.True(Directory.Exists(attemptedPath));
        Assert.Equal(pending, fixture.Store.Usage);

        var verifiedPath = Path.GetFullPath(attemptedPath!);
        Assert.StartsWith(Path.GetFullPath(fixture.DirectoryPath) + Path.DirectorySeparatorChar, verifiedPath);
        Directory.Delete(verifiedPath, recursive: false);
        fixture.Spool.CleanupExpired(); // Only actual filesystem cleanup can refund the retained descriptor.
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
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
        var reservation = fixture.ReservePayload(payload.LongLength, maxLiveBuffers: 1, containers: 1);
        var credits = fixture.UsedCredits;
        var error = Assert.Throws<SnapshotPersistenceException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.DoesNotContain(fixture.DirectoryPath, error.Message);
        Assert.Contains("injected private partial allocation", Assert.IsType<IOException>(error.InnerException).Message);
        Assert.NotNull(attemptedPath);
        Assert.True(File.Exists(attemptedPath));
        Assert.True(offeredLength > 0);
        Assert.Equal(offeredLength, new FileInfo(attemptedPath!).Length);
        var ownerPath = Path.Combine(Path.GetDirectoryName(attemptedPath!)!, "owner.lock");
        Assert.Equal(checked(offeredLength + new FileInfo(ownerPath).Length), fixture.Store.Usage.PhysicalBytes);
        Assert.Equal(credits, fixture.UsedCredits);
        var retained = fixture.Store.Usage;
        fixture.Spool.Dispose();
        Assert.Equal(retained, fixture.Store.Usage);
        Assert.True(File.Exists(attemptedPath));
        AssertOwnerLockHeld(ownerPath);
        reservation.Dispose();
        Assert.False(File.Exists(attemptedPath));
        Assert.False(File.Exists(ownerPath));
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public void CancellationAndDisposedReservationRejectCaptureWithoutReleasingLiveReaders()
    {
        using var fixture = new Fixture(Settings);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var payload = Payload(Settings.SmallPayloadBytes);
        var reservation = fixture.ReservePayload(checked(payload.LongLength * 2), maxLiveBuffers: 2, containers: 2,
            cancellationToken: cancel.Token);
        var lease = fixture.Hold(reservation.CaptureUtf8(payload, cancel.Token));
        var raw = fixture.Hold(reservation.AuditCapture.CreateStaging(cancel.Token));
        raw.Write(payload);
        var historyReader = fixture.Hold(lease.Read());
        var rawReader = fixture.Hold(raw.OpenSnapshotRead());
        var before = fixture.Store.Usage;
        cancel.Cancel();
        var minimumCredits = fixture.Store.EstimateCredits(0, 0, 0);
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Store.Reserve(minimumCredits, cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reservation.AuditCapture.CreateStaging(fixture.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => reservation.Extend(minimumCredits, fixture.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => raw.Write(payload));
        Assert.Equal(before, fixture.Store.Usage);
        fixture.AssertLedgerWithinRoot();
        reservation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reservation.CaptureUtf8(payload, fixture.Token));
        Assert.Throws<ObjectDisposedException>(() => reservation.AuditCapture.CreateStaging(fixture.Token));
        Assert.Throws<ObjectDisposedException>(() => reservation.Extend(minimumCredits, fixture.Token));
        raw.Dispose();
        lease.Dispose();
        fixture.Spool.Dispose();
        Assert.Equal(payload, ReadAll(historyReader));
        Assert.Equal(payload, ReadAll(rawReader));
        historyReader.Dispose();
        Assert.True(fixture.Store.Usage.PhysicalBytes > 0);
        rawReader.Dispose();
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
        fixture.CloseAndAssertEmpty();
    }

    [Fact]
    public async Task ConcurrentReservationsAndDisposalKeepAtomicLedgerWithinRootCapacity()
    {
        using var fixture = new Fixture(Settings);
        Assert.True(Settings.ConcurrentWorkers > 1);
        var minimumCredits = fixture.Store.EstimateCredits(0, 0, 0);
        var credits = checked(fixture.Store.MaxBytes / Settings.ConcurrentWorkers + minimumCredits);
        Assert.True(credits > minimumCredits);
        var accepted = new SnapshotReservation?[Settings.ConcurrentWorkers];
        var rejected = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, Settings.ConcurrentWorkers).Select(worker => Task.Run(async () =>
        {
            await start.Task.WaitAsync(fixture.Token);
            try
            {
                accepted[worker] = fixture.Hold(fixture.Store.Reserve(credits, fixture.Token));
                fixture.AssertLedgerWithinRoot();
            }
            catch (SnapshotCapacityExceededException error)
            {
                Assert.True(error.RequestedBytes > error.AvailableBytes);
                Assert.InRange(error.AvailableBytes, 0L, fixture.Store.MaxBytes);
                Interlocked.Increment(ref rejected);
                fixture.AssertLedgerWithinRoot();
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts).WaitAsync(fixture.Token);
        var succeeded = accepted.Count(reservation => reservation is not null);
        Assert.InRange(succeeded, 1, Settings.ConcurrentWorkers - 1);
        Assert.Equal(Settings.ConcurrentWorkers - succeeded, rejected);
        Assert.Equal(checked(succeeded * credits), fixture.UsedCredits);
        Assert.Equal(0L, fixture.Store.Usage.PhysicalBytes); // Near-root capacity is logical only.
        Assert.Equal(0, fixture.Store.Usage.Containers);

        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = accepted.Where(reservation => reservation is not null).Select(reservation => Task.Run(async () =>
        {
            await releaseStart.Task.WaitAsync(fixture.Token);
            reservation!.Dispose();
            fixture.AssertLedgerWithinRoot();
            using var next = fixture.Store.Reserve(minimumCredits, fixture.Token);
            fixture.AssertLedgerWithinRoot();
        })).ToArray();
        releaseStart.SetResult();
        await Task.WhenAll(releases).WaitAsync(fixture.Token);
        fixture.Spool.Dispose();
        Assert.Equal((0L, 0L, 0L, 0), fixture.Store.Usage);
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
        public long UsedCredits
        {
            get
            {
                var usage = Store.Usage;
                return checked(usage.ReservedBytes + usage.PhysicalBytes + usage.MetadataBytes);
            }
        }

        public T Hold<T>(T holder) where T : IDisposable
        {
            lock (_holders) _holders.Add(holder);
            return holder;
        }

        public SnapshotReservation ReservePayload(long physicalPayloadBytes, int maxLiveBuffers, int containers,
            CancellationToken cancellationToken = default)
            => Hold(Store.Reserve(Store.EstimateCredits(physicalPayloadBytes, maxLiveBuffers, containers),
                cancellationToken == default ? Token : cancellationToken));

        public void AssertLedgerWithinRoot()
        {
            var usage = Store.Usage;
            Assert.InRange(usage.ReservedBytes, 0L, Store.MaxBytes);
            Assert.InRange(usage.PhysicalBytes, 0L, Store.MaxBytes);
            Assert.InRange(usage.MetadataBytes, 0L, Store.MaxBytes);
            Assert.InRange(usage.Containers, 0, int.MaxValue);
            Assert.InRange(checked(usage.ReservedBytes + usage.PhysicalBytes + usage.MetadataBytes), 0L, Store.MaxBytes);
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
            Assert.Equal((0L, 0L, 0L, 0), Store.Usage);
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
