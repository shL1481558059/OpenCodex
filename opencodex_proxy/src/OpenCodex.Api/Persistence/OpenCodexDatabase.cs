using Microsoft.Data.Sqlite;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    public static void Initialize(string dbPath, string defaultOwnerUsername = "admin")
    {
        var normalizedDefaultOwner = NormalizeUsername(defaultOwnerUsername);
        if (normalizedDefaultOwner.Length == 0)
        {
            normalizedDefaultOwner = "admin";
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection(dbPath);
        ExecuteNonQuery(connection, Schema);
        MigrateRequestLogs(connection, normalizedDefaultOwner);
        MigrateChannels(connection, normalizedDefaultOwner);
        MigrateWebSearch(connection);
        ExecuteNonQuery(connection, RequestLogsIndexesSchema);
    }

    internal static SqliteConnection OpenRepositoryConnection(string dbPath)
    {
        return OpenConnection(dbPath);
    }

    internal static string NormalizeRepositoryUsername(object? value)
    {
        return NormalizeUsername(value);
    }

    internal static IReadOnlyDictionary<string, object?> ParseRepositoryJsonObject(string? raw)
    {
        return ParseJsonObject(raw);
    }

    internal static IReadOnlyList<object?> ParseRepositoryJsonList(string? raw)
    {
        return ParseJsonList(raw);
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

}
