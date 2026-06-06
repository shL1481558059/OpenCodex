using Microsoft.Data.Sqlite;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static void AddOwnerParameter(SqliteCommand command, string? ownerUsername)
    {
        if (!string.IsNullOrWhiteSpace(ownerUsername))
        {
            command.Parameters.AddWithValue("$owner_username", ownerUsername);
        }
    }

    private static int ReadInt(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static double ReadDouble(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal));
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));
    }

    private static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? GetNullableDouble(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static void AddNullableInt64(SqliteCommand command, string name, long? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value.Value);
    }

    private static void AddNullableInt32(SqliteCommand command, string name, int? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value.Value);
    }

    private static void AddNullableDouble(SqliteCommand command, string name, double? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value.Value);
    }

    private static void AddNullableString(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
