using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.LogMaintenance;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Data;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class StreamLineLogCleanupServiceTests
{
    private static readonly RequestLogContentSlot LegacyStreamLinesSlot = (RequestLogContentSlot)8;

    [Fact]
    public async Task ExecuteAsync_DryRunReportsAndKeepsLegacyContent()
    {
        var dbPath = CreateDatabasePath();
        await using var context = CreateContext(dbPath);
        var logId = SeedLog(context, "req-dry-run");
        new LogContentStore(context).Write(logId, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestBody] = "{\"keep\":true}",
            [LegacyStreamLinesSlot] = "[{\"sequence\":0}]"
        });

        var result = await new StreamLineLogCleanupService(context).ExecuteAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.ContentRefs);
        Assert.Equal(1, result.Manifests);
        Assert.True(result.ManifestChunks > 0);
        Assert.True(result.Blocks > 0);
        Assert.True(result.RawBytes > 0);

        var remainingSlots = await context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == logId)
            .Select(reference => (short)reference.Slot)
            .ToListAsync();
        Assert.Contains((short)LegacyStreamLinesSlot, remainingSlots);
        Assert.Contains((short)RequestLogContentSlot.RequestBody, remainingSlots);
    }

    [Fact]
    public async Task ExecuteAsync_RemovesLegacyContentAndKeepsOtherSlots()
    {
        var dbPath = CreateDatabasePath();
        await using var context = CreateContext(dbPath);
        var logId = SeedLog(context, "req-cleanup");
        new LogContentStore(context).Write(logId, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestBody] = "{\"keep\":true}",
            [LegacyStreamLinesSlot] = "[{\"sequence\":0}]"
        });

        var result = await new StreamLineLogCleanupService(context).ExecuteAsync(dryRun: false);

        Assert.False(result.DryRun);
        Assert.Equal(1, result.ContentRefs);
        var remainingSlots = await context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == logId)
            .Select(reference => (short)reference.Slot)
            .ToListAsync();
        Assert.DoesNotContain((short)LegacyStreamLinesSlot, remainingSlots);
        Assert.Contains((short)RequestLogContentSlot.RequestBody, remainingSlots);

        var content = new LogContentStore(context).Read(logId);
        Assert.Contains("keep", content.Get(RequestLogContentSlot.RequestBody), StringComparison.Ordinal);

        var second = await new StreamLineLogCleanupService(context).ExecuteAsync(dryRun: false);
        Assert.Equal(0, second.ContentRefs);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsBlocksSharedWithOtherSlots()
    {
        var dbPath = CreateDatabasePath();
        await using var context = CreateContext(dbPath);
        var legacyLogId = SeedLog(context, "req-legacy");
        var liveLogId = SeedLog(context, "req-live");
        var store = new LogContentStore(context);
        store.Write(legacyLogId, new Dictionary<RequestLogContentSlot, string?>
        {
            [LegacyStreamLinesSlot] = "shared-content"
        });
        store.Write(liveLogId, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestBody] = "shared-content"
        });

        var result = await new StreamLineLogCleanupService(context).ExecuteAsync(dryRun: false);

        Assert.Equal(1, result.ContentRefs);
        Assert.Equal(0, result.Manifests);
        Assert.Equal(0, result.Blocks);
        var liveContent = store.Read(liveLogId).Get(RequestLogContentSlot.RequestBody);
        Assert.Equal("shared-content", liveContent);
        Assert.Empty(await context.RequestLogContentRefs
            .Where(reference => reference.RequestLogId == legacyLogId)
            .ToListAsync());
    }

    private static string CreateDatabasePath()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-stream-line-cleanup-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return dbPath;
    }

    private static IOpenCodexDbContext CreateContext(string dbPath)
    {
        var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        return context;
    }

    private static Guid SeedLog(IOpenCodexDbContext context, string requestId)
    {
        var ownerUserId = Guid.NewGuid();
        context.Users.Add(new User
        {
            Id = ownerUserId,
            Username = $"owner-{requestId}",
            PasswordHash = "hash",
            Role = "user",
            Enabled = true,
            CreatedAt = 1,
            UpdatedAt = 1
        });
        var logId = Guid.NewGuid();
        context.RequestLogs.Add(new RequestLog
        {
            Id = logId,
            RequestId = requestId,
            CreatedAt = 1_700_000_000,
            Method = "POST",
            Path = "/v1/responses",
            RequestType = ProxyRequestTypes.Main,
            IsStream = true,
            OwnerUserId = ownerUserId
        });
        context.SaveChanges();
        return logId;
    }
}
