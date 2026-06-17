using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexRequestLogs
{
    public static void EnsureSchema(OpenCodexDbContext context)
    {
        AddColumnIfMissing(context, "RequestLogs", "RequestType", """ALTER TABLE "RequestLogs" ADD COLUMN "RequestType" TEXT NOT NULL DEFAULT 'main';""");
        AddColumnIfMissing(context, "RequestLogs", "ProcessingStartedAt", """ALTER TABLE "RequestLogs" ADD COLUMN "ProcessingStartedAt" REAL NULL;""");
        AddColumnIfMissing(context, "RequestLogs", "CompletedAt", """ALTER TABLE "RequestLogs" ADD COLUMN "CompletedAt" REAL NULL;""");
        AddColumnIfMissing(context, "RequestLogs", "LifecycleStatus", """ALTER TABLE "RequestLogs" ADD COLUMN "LifecycleStatus" TEXT NULL;""");
        AddColumnIfMissing(context, "RequestLogs", "ParentRequestLogId", """ALTER TABLE "RequestLogs" ADD COLUMN "ParentRequestLogId" INTEGER NULL;""");
        AddColumnIfMissing(context, "RequestLogDetails", "OcrJson", """ALTER TABLE "RequestLogDetails" ADD COLUMN "OcrJson" TEXT NULL;""");
        AddColumnIfMissing(context, "RequestLogDetails", "StreamTimingsJson", """ALTER TABLE "RequestLogDetails" ADD COLUMN "StreamTimingsJson" TEXT NULL;""");
        EnsureStreamLinesTable(context);

        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogs_RequestType" ON "RequestLogs" ("RequestType");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogs_LifecycleStatus" ON "RequestLogs" ("LifecycleStatus");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogs_ParentRequestLogId" ON "RequestLogs" ("ParentRequestLogId");""");
    }

    private static void EnsureStreamLinesTable(OpenCodexDbContext context)
    {
        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS "RequestLogStreamLines" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RequestLogStreamLines" PRIMARY KEY AUTOINCREMENT,
                "RequestLogId" INTEGER NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "OccurredAt" REAL NOT NULL,
                "Source" TEXT NOT NULL,
                "RawLine" TEXT NOT NULL,
                CONSTRAINT "FK_RequestLogStreamLines_RequestLogs_RequestLogId"
                    FOREIGN KEY ("RequestLogId") REFERENCES "RequestLogs" ("Id") ON DELETE CASCADE
            );
            """);
        context.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_RequestLogStreamLines_RequestLogId_Sequence" ON "RequestLogStreamLines" ("RequestLogId", "Sequence");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogStreamLines_OccurredAt" ON "RequestLogStreamLines" ("OccurredAt");""");
    }

    private static void AddColumnIfMissing(
        OpenCodexDbContext context,
        string tableName,
        string columnName,
        string sql)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        if (command.Connection?.State != System.Data.ConnectionState.Open)
        {
            command.Connection?.Open();
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.Ordinal))
            {
                return;
            }
        }

        context.Database.ExecuteSqlRaw(sql);
    }
}
