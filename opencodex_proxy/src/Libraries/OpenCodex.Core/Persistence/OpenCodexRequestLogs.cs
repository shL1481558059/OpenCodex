using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexRequestLogs
{
    public static void EnsureSchema(OpenCodexDbContext context)
    {
        AddColumnIfMissing(context, "RequestLogs", "RequestType", """ALTER TABLE "RequestLogs" ADD COLUMN "RequestType" TEXT NOT NULL DEFAULT 'main';""");
        AddColumnIfMissing(context, "RequestLogs", "ParentRequestLogId", """ALTER TABLE "RequestLogs" ADD COLUMN "ParentRequestLogId" INTEGER NULL;""");
        AddColumnIfMissing(context, "RequestLogDetails", "OcrJson", """ALTER TABLE "RequestLogDetails" ADD COLUMN "OcrJson" TEXT NULL;""");

        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogs_RequestType" ON "RequestLogs" ("RequestType");""");
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_RequestLogs_ParentRequestLogId" ON "RequestLogs" ("ParentRequestLogId");""");
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
