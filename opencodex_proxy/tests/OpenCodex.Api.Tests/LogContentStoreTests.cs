using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Data;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class LogContentStoreTests
{
    [Fact]
    public void IdenticalValuesAcrossRequests_ShareManifestAndBlocks()
    {
        var (dbPath, firstLogId, secondLogId) = CreateDatabaseWithTwoLogs();
        var value = BuildConversation(180);

        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            var store = new LogContentStore(context);
            store.Write(firstLogId, Values(value));
            store.Write(secondLogId, Values(value));

            Assert.Equal(value, store.Read(firstLogId).Get(RequestLogContentSlot.RequestBody));
            Assert.Equal(value, store.Read(secondLogId).Get(RequestLogContentSlot.RequestBody));
            var references = context.RequestLogContentRefs
                .Where(reference => reference.Slot == RequestLogContentSlot.RequestBody)
                .ToList();
            Assert.Equal(2, references.Count);
            Assert.Single(references.Select(reference => reference.ManifestId).Distinct());
            Assert.Single(context.LogContentManifests);
            Assert.True(context.LogContentBlocks.Count() > 1);
        }
    }

    [Fact]
    public void AppendAndEdit_CreateImmutableBranchesThatReuseUnchangedBlocks()
    {
        var dbPath = CreateDatabase();
        Guid originalLogId;
        Guid appendedLogId;
        Guid editedLogId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            originalLogId = AddLog(context, "original");
            appendedLogId = AddLog(context, "appended");
            editedLogId = AddLog(context, "edited-branch");
            context.SaveChanges();
        }

        var original = BuildConversation(220);
        var appended = BuildConversation(221);
        var edited = BuildConversation(220, editedTurn: 70);
        using var readContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(readContext);
        store.Write(originalLogId, Values(original));
        store.Write(appendedLogId, Values(appended));
        store.Write(editedLogId, Values(edited));

        var originalBlocks = ReadBlockIds(readContext, originalLogId);
        var appendedBlocks = ReadBlockIds(readContext, appendedLogId);
        var editedBlocks = ReadBlockIds(readContext, editedLogId);

        Assert.Equal(original, store.Read(originalLogId).Get(RequestLogContentSlot.RequestBody));
        Assert.Equal(appended, store.Read(appendedLogId).Get(RequestLogContentSlot.RequestBody));
        Assert.Equal(edited, store.Read(editedLogId).Get(RequestLogContentSlot.RequestBody));
        Assert.True(originalBlocks.Intersect(appendedBlocks).Count() >= originalBlocks.Count - 1);
        Assert.True(originalBlocks.Intersect(editedBlocks).Count() >= originalBlocks.Count / 2);
        Assert.NotEqual(
            readContext.RequestLogContentRefs.Single(reference => reference.RequestLogId == originalLogId).ManifestId,
            readContext.RequestLogContentRefs.Single(reference => reference.RequestLogId == editedLogId).ManifestId);
    }

    [Fact]
    public void ReplacingOneReference_DoesNotMutateOtherRequestOrSharedManifest()
    {
        var (dbPath, firstLogId, secondLogId) = CreateDatabaseWithTwoLogs();
        var original = BuildConversation(140);
        var replacement = BuildConversation(141);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(context);
        store.Write(firstLogId, Values(original));
        store.Write(secondLogId, Values(original));
        var sharedManifestId = context.RequestLogContentRefs
            .Single(reference => reference.RequestLogId == firstLogId)
            .ManifestId;

        store.Write(secondLogId, Values(replacement));

        Assert.Equal(original, store.Read(firstLogId).Get(RequestLogContentSlot.RequestBody));
        Assert.Equal(replacement, store.Read(secondLogId).Get(RequestLogContentSlot.RequestBody));
        Assert.Equal(
            sharedManifestId,
            context.RequestLogContentRefs.Single(reference => reference.RequestLogId == firstLogId).ManifestId);
    }

    [Fact]
    public void ReplacingLastReference_RemovesOrphanedManifestAndBlocks()
    {
        var dbPath = CreateDatabase();
        Guid logId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            logId = AddLog(context, "replace-last-reference");
            context.SaveChanges();
        }

        var original = BuildConversation(180);
        var replacement = BuildConversation(180, editedTurn: 70);
        using var readContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(readContext);
        store.Write(logId, Values(original));
        var originalManifestId = readContext.RequestLogContentRefs.Single().ManifestId;

        store.Write(logId, Values(replacement));

        Assert.Equal(replacement, store.Read(logId).Get(RequestLogContentSlot.RequestBody));
        Assert.DoesNotContain(
            readContext.LogContentManifests,
            manifest => manifest.Id == originalManifestId);
        Assert.DoesNotContain(
            readContext.LogContentBlocks,
            block => !readContext.LogContentManifestChunks.Any(chunk => chunk.BlockId == block.Id));
    }

    [Fact]
    public void EmptyStringAndNull_AreStoredAsDifferentStates()
    {
        var dbPath = CreateDatabase();
        Guid logId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            logId = AddLog(context, "empty-null");
            context.SaveChanges();
        }

        using var readContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(readContext);
        store.Write(logId, new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.ResponseBody] = string.Empty,
            [RequestLogContentSlot.WebSearchJson] = null
        });

        var snapshot = store.Read(logId);
        Assert.Equal(string.Empty, snapshot.Get(RequestLogContentSlot.ResponseBody));
        Assert.Null(snapshot.Get(RequestLogContentSlot.WebSearchJson));
        var manifestId = readContext.RequestLogContentRefs.Single().ManifestId;
        var manifest = readContext.LogContentManifests.Single(item => item.Id == manifestId);
        Assert.Equal(0, manifest.RawLength);
        Assert.Equal(0, manifest.ChunkCount);
    }

    [Fact]
    public void DeduplicatedBlockLookup_SqlDoesNotSelectDataColumn()
    {
        var (dbPath, firstLogId, secondLogId) = CreateDatabaseWithTwoLogs();
        var value = BuildConversation(180);
        var probe = new BlockSqlProbe();

        using (var context = CreateCapturingContext(dbPath, probe))
        {
            var store = new LogContentStore(context);
            store.Write(firstLogId, Values(value));
        }

        probe.Reset();
        using (var context = CreateCapturingContext(dbPath, probe))
        {
            var store = new LogContentStore(context);
            store.Write(secondLogId, Values(value));
        }

        var lookupSql = probe.Commands
            .FirstOrDefault(command => command.Contains("LogContentBlocks", StringComparison.OrdinalIgnoreCase)
                && command.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lookupSql);
        Assert.DoesNotContain("Data", lookupSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdenticalContentWrittenTwice_DoesNotAddBlocksAndSharesManifest()
    {
        var (dbPath, firstLogId, secondLogId) = CreateDatabaseWithTwoLogs();
        var value = BuildConversation(120);

        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(context);
        store.Write(firstLogId, Values(value));
        var blockCountAfterFirstWrite = context.LogContentBlocks.Count();
        var firstManifestId = context.RequestLogContentRefs.Single(
            reference => reference.RequestLogId == firstLogId).ManifestId;

        store.Write(secondLogId, Values(value));

        Assert.Equal(blockCountAfterFirstWrite, context.LogContentBlocks.Count());
        Assert.Equal(2, context.RequestLogContentRefs.Count());
        Assert.All(
            context.RequestLogContentRefs.ToList(),
            reference => Assert.Equal(firstManifestId, reference.ManifestId));
    }

    [Fact]
    public void HashCollision_WithDifferentRawLength_ThrowsInvalidData()
    {
        var dbPath = CreateDatabase();
        Guid logId;
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            logId = AddLog(context, "hash-collision");
            context.SaveChanges();
        }

        var encoded = LogContentCodec.Encode(BuildConversation(10));
        var collisionChunk = encoded.Chunks.First();
        var collisionBlock = new LogContentBlock
        {
            Id = Guid.NewGuid(),
            Sha256 = collisionChunk.Hash,
            RawLength = collisionChunk.OriginalLength + 1,
            StoredLength = collisionChunk.Data.Length,
            Compression = collisionChunk.Codec,
            Data = collisionChunk.Data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };
        using (var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}"))
        {
            context.LogContentBlocks.Add(collisionBlock);
            context.SaveChanges();
        }

        using var readContext = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var store = new LogContentStore(readContext);
        var exception = Assert.Throws<InvalidDataException>(
            () => store.Write(logId, Values(BuildConversation(10))));
        Assert.Contains("Content hash collision detected for block", exception.Message);
    }

    private static (string DbPath, Guid FirstLogId, Guid SecondLogId) CreateDatabaseWithTwoLogs()
    {
        var dbPath = CreateDatabase();
        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        var firstLogId = AddLog(context, "first");
        var secondLogId = AddLog(context, "second");
        context.SaveChanges();
        return (dbPath, firstLogId, secondLogId);
    }

    private static string CreateDatabase()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "opencodex-content-store-tests",
            $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var context = OpenCodexDbContextFactory.Create("sqlite", $"Data Source={dbPath}");
        context.Database.Migrate();
        return dbPath;
    }

    private static Guid AddLog(IOpenCodexDbContext context, string requestId)
    {
        var log = new RequestLog
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            RequestType = ProxyRequestTypes.Main,
            OwnerUserId = Guid.Empty
        };
        context.RequestLogs.Add(log);
        return log.Id;
    }

    private static IReadOnlyDictionary<RequestLogContentSlot, string?> Values(string value)
    {
        return new Dictionary<RequestLogContentSlot, string?>
        {
            [RequestLogContentSlot.RequestBody] = value
        };
    }

    private static OpenCodexSqliteDbContext CreateCapturingContext(string dbPath, BlockSqlProbe probe)
    {
        var builder = new DbContextOptionsBuilder<OpenCodexSqliteDbContext>();
        OpenCodexDbContextFactory.ConfigureSqlite(builder, $"Data Source={dbPath}");
        builder.AddInterceptors(probe);
        return new OpenCodexSqliteDbContext(builder.Options);
    }

    private static List<Guid> ReadBlockIds(IOpenCodexDbContext context, Guid logId)
    {
        var manifestId = context.RequestLogContentRefs
            .Single(reference => reference.RequestLogId == logId
                && reference.Slot == RequestLogContentSlot.RequestBody)
            .ManifestId;
        return context.LogContentManifestChunks
            .Where(chunk => chunk.ManifestId == manifestId)
            .OrderBy(chunk => chunk.Ordinal)
            .Select(chunk => chunk.BlockId)
            .ToList();
    }

    private static string BuildConversation(int turnCount, int? editedTurn = null)
    {
        var messages = Enumerable.Range(0, turnCount)
            .Select(index => new Dictionary<string, object?>
            {
                ["role"] = index % 2 == 0 ? "user" : "assistant",
                ["content"] = index == editedTurn
                    ? $"edited-{index:D4}:{DeterministicText(9000 + index, 420)}"
                    : $"turn-{index:D4}:{DeterministicText(index, 320)}"
            })
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "gpt-5",
            ["stream"] = true,
            ["messages"] = messages
        });
    }

    private static string DeterministicText(int seed, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[length];
        uint state = unchecked((uint)seed + 0x9e3779b9U);
        for (var index = 0; index < chars.Length; index++)
        {
            state = unchecked(state * 1664525U + 1013904223U);
            chars[index] = alphabet[(int)(state % alphabet.Length)];
        }

        return new string(chars);
    }

    private sealed class BlockSqlProbe : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_sync)
                {
                    return _commands.ToArray();
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _commands.Clear();
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            lock (_sync)
            {
                _commands.Add(command.CommandText);
            }

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _commands.Add(command.CommandText);
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
