using Microsoft.EntityFrameworkCore;
using OpenCodex.Data;

namespace OpenCodex.Core.Persistence;

public static class OpenCodexChannels
{
    private const int RequiredCapacityDefault = 3;

    public static void EnsureSchema(OpenCodexDbContext context)
    {
        var priorityAdded = AddColumnIfMissing(
            context,
            "Channels",
            "Priority",
            """ALTER TABLE "Channels" ADD COLUMN "Priority" INTEGER NOT NULL DEFAULT 0;""");
        AddColumnIfMissing(
            context,
            "Channels",
            "Capacity",
            $"""ALTER TABLE "Channels" ADD COLUMN "Capacity" INTEGER NOT NULL DEFAULT {RequiredCapacityDefault};""");

        if (priorityAdded)
        {
            context.Database.ExecuteSqlRaw("""UPDATE "Channels" SET "Priority" = "Position";""");
        }

        context.Database.ExecuteSqlRaw($"""UPDATE "Channels" SET "Capacity" = {RequiredCapacityDefault} WHERE "Capacity" IS NULL;""");

        context.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_Channels_OwnerUsername_Priority_Position_Id" ON "Channels" ("OwnerUsername", "Priority", "Position", "Id");""");
    }

    private static bool AddColumnIfMissing(
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
                return false;
            }
        }

        context.Database.ExecuteSqlRaw(sql);
        return true;
    }
}
