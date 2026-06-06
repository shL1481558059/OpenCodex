using Microsoft.Data.Sqlite;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static void MigrateRequestLogs(SqliteConnection connection, string defaultOwnerUsername)
    {
        AddColumnIfMissing(connection, "request_logs", "owner_username", "TEXT");
        AddColumnIfMissing(connection, "request_logs", "api_key_id", "INTEGER");
        AddColumnIfMissing(connection, "request_log_details", "request_headers", "TEXT");
        AddColumnIfMissing(connection, "request_log_details", "request_body", "TEXT");
        AddColumnIfMissing(connection, "request_log_details", "upstream_request_body", "TEXT");
        AddColumnIfMissing(connection, "request_log_details", "upstream_response_body", "TEXT");
        AddColumnIfMissing(connection, "request_log_details", "response_body", "TEXT");
        AddColumnIfMissing(connection, "request_log_details", "web_search_json", "TEXT");

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE request_logs
            SET owner_username = $owner_username
            WHERE owner_username IS NULL OR owner_username = ''
            """;
        command.Parameters.AddWithValue("$owner_username", defaultOwnerUsername);
        command.ExecuteNonQuery();
    }

    private static void MigrateChannels(SqliteConnection connection, string defaultOwnerUsername)
    {
        AddColumnIfMissing(connection, "channels", "models_json", "TEXT NOT NULL DEFAULT '[]'");
        AddColumnIfMissing(connection, "channels", "retry_count", "INTEGER NOT NULL DEFAULT 3");
        AddColumnIfMissing(connection, "channels", "owner_username", "TEXT");

        using (var ownerCommand = connection.CreateCommand())
        {
            ownerCommand.CommandText = """
                UPDATE channels
                SET owner_username = $owner_username
                WHERE owner_username IS NULL OR owner_username = ''
                """;
            ownerCommand.Parameters.AddWithValue("$owner_username", defaultOwnerUsername);
            ownerCommand.ExecuteNonQuery();
        }

        using (var authCommand = connection.CreateCommand())
        {
            authCommand.CommandText = """
                UPDATE channels
                SET auth_mode = 'config'
                WHERE auth_mode IS NULL
                   OR auth_mode = ''
                   OR auth_mode NOT IN ('config', 'none')
                """;
            authCommand.ExecuteNonQuery();
        }

        if (!ChannelPrimaryKey(connection).SequenceEqual(["owner_username", "id"]))
        {
            RebuildChannelsWithOwnerPrimaryKey(connection, defaultOwnerUsername);
        }

        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS idx_channels_owner_position ON channels(owner_username, position)");
    }

    private static void MigrateWebSearch(SqliteConnection connection)
    {
        AddColumnIfMissing(connection, "web_search_settings", "key_usage_limit", "INTEGER NOT NULL DEFAULT 1000");
        AddColumnIfMissing(connection, "tavily_keys", "provider", "TEXT NOT NULL DEFAULT 'tavily'");
        AddColumnIfMissing(connection, "tavily_keys", "usage_limit", "INTEGER NOT NULL DEFAULT 1000");
        AddColumnIfMissing(connection, "access_api_keys", "key_plaintext", "TEXT");
    }

    private static void RebuildChannelsWithOwnerPrimaryKey(SqliteConnection connection, string defaultOwnerUsername)
    {
        ExecuteNonQuery(connection, "ALTER TABLE channels RENAME TO channels_legacy");
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE channels (
                owner_username TEXT NOT NULL DEFAULT 'admin',
                id TEXT NOT NULL,
                position INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                type TEXT NOT NULL,
                baseurl TEXT NOT NULL,
                apikey TEXT NOT NULL DEFAULT '',
                auth_mode TEXT NOT NULL DEFAULT 'config',
                headers_json TEXT NOT NULL DEFAULT '{}',
                timeout_seconds INTEGER NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 3,
                compat_json TEXT NOT NULL DEFAULT '{}',
                models_json TEXT NOT NULL DEFAULT '[]',
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at REAL NOT NULL,
                updated_at REAL NOT NULL,
                PRIMARY KEY (owner_username, id)
            )
            """);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channels (
                owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                headers_json, timeout_seconds, retry_count, compat_json, models_json,
                enabled, created_at, updated_at
            )
            SELECT
                COALESCE(NULLIF(owner_username, ''), $owner_username),
                id,
                position,
                name,
                type,
                baseurl,
                apikey,
                CASE
                    WHEN auth_mode IS NULL
                      OR auth_mode = ''
                      OR auth_mode NOT IN ('config', 'none')
                    THEN 'config'
                    ELSE auth_mode
                END,
                headers_json,
                timeout_seconds,
                retry_count,
                compat_json,
                models_json,
                enabled,
                created_at,
                updated_at
            FROM channels_legacy
            """;
        command.Parameters.AddWithValue("$owner_username", defaultOwnerUsername);
        command.ExecuteNonQuery();
        ExecuteNonQuery(connection, "DROP TABLE channels_legacy");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        if (ColumnNames(connection, tableName).Contains(columnName, StringComparer.Ordinal))
        {
            return;
        }

        ExecuteNonQuery(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}");
    }

    private static HashSet<string> ColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static List<string> ChannelPrimaryKey(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(channels)";
        using var reader = command.ExecuteReader();
        var keyedColumns = new List<(int Position, string Name)>();
        while (reader.Read())
        {
            var primaryKeyPosition = reader.GetInt32(5);
            if (primaryKeyPosition > 0)
            {
                keyedColumns.Add((primaryKeyPosition, reader.GetString(1)));
            }
        }

        return keyedColumns
            .OrderBy(item => item.Position)
            .Select(item => item.Name)
            .ToList();
    }
}
