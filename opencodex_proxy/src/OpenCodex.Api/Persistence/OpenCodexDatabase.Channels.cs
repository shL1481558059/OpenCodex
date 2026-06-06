using Microsoft.Data.Sqlite;
using OpenCodex.Api.Config;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private const int DefaultRetryCount = OpenCodexConfig.DefaultRetryCount;

    public static IReadOnlyList<ChannelRecord> ReadChannels(
        string dbPath,
        string? ownerUsername = null,
        string defaultOwnerUsername = "admin")
    {
        Initialize(dbPath, defaultOwnerUsername);

        var normalizedOwner = ownerUsername is null ? null : NormalizeUsername(ownerUsername);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = normalizedOwner is { Length: > 0 }
            ? """
              SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                     headers_json, timeout_seconds, retry_count, compat_json, models_json, enabled
              FROM channels
              WHERE owner_username = $owner_username
              ORDER BY position ASC, id ASC
              """
            : """
              SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                     headers_json, timeout_seconds, retry_count, compat_json, models_json, enabled
              FROM channels
              ORDER BY owner_username ASC, position ASC, id ASC
              """;
        if (normalizedOwner is { Length: > 0 })
        {
            command.Parameters.AddWithValue("$owner_username", normalizedOwner);
        }

        using var reader = command.ExecuteReader();
        var channels = new List<ChannelRecord>();
        while (reader.Read())
        {
            channels.Add(ReadChannel(reader));
        }

        return channels;
    }

    public static void ReplaceChannels(
        string dbPath,
        IEnumerable<IReadOnlyDictionary<string, object?>> channels,
        int defaultTimeout = 120,
        string? ownerUsername = "admin",
        string defaultOwnerUsername = "admin")
    {
        var normalizedDefaultOwner = NormalizeUsername(defaultOwnerUsername);
        if (normalizedDefaultOwner.Length == 0)
        {
            normalizedDefaultOwner = "admin";
        }

        var normalizedOwner = ownerUsername is null ? null : NormalizeUsername(ownerUsername);
        Initialize(dbPath, normalizedDefaultOwner);

        using var connection = OpenConnection(dbPath);
        var existingCreated = ReadExistingChannelCreatedTimes(connection);
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                if (normalizedOwner is null)
                {
                    deleteCommand.CommandText = "DELETE FROM channels";
                }
                else
                {
                    deleteCommand.CommandText = "DELETE FROM channels WHERE owner_username = $owner_username";
                    deleteCommand.Parameters.AddWithValue("$owner_username", normalizedOwner);
                }

                deleteCommand.ExecuteNonQuery();
            }

            var now = UnixTimeSeconds();
            var position = 0;
            foreach (var channel in channels)
            {
                var channelOwner = normalizedOwner
                    ?? NormalizeUsername(GetOptionalValue(channel, "owner_username"))
                    ?? normalizedDefaultOwner;
                if (channelOwner.Length == 0)
                {
                    channelOwner = normalizedDefaultOwner;
                }

                var id = RequiredString(channel, "id");
                var key = (channelOwner, id);
                var createdAt = existingCreated.TryGetValue(key, out var existingCreatedAt)
                    ? existingCreatedAt
                    : now;

                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO channels (
                        owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                        headers_json, timeout_seconds, retry_count, compat_json,
                        models_json, enabled, created_at, updated_at
                    ) VALUES (
                        $owner_username, $id, $position, $name, $type, $baseurl,
                        $apikey, $auth_mode, $headers_json, $timeout_seconds,
                        $retry_count, $compat_json, $models_json, $enabled,
                        $created_at, $updated_at
                    )
                    """;
                insertCommand.Parameters.AddWithValue("$owner_username", channelOwner);
                insertCommand.Parameters.AddWithValue("$id", id);
                insertCommand.Parameters.AddWithValue("$position", position);
                insertCommand.Parameters.AddWithValue("$name", OptionalString(channel, "name", string.Empty));
                insertCommand.Parameters.AddWithValue("$type", RequiredString(channel, "type"));
                insertCommand.Parameters.AddWithValue("$baseurl", RequiredString(channel, "baseurl"));
                insertCommand.Parameters.AddWithValue("$apikey", OptionalString(channel, "apikey", string.Empty));
                insertCommand.Parameters.AddWithValue("$auth_mode", OptionalString(channel, "auth_mode", "config"));
                insertCommand.Parameters.AddWithValue("$headers_json", JsonDumps(GetOptionalValue(channel, "headers") ?? new Dictionary<string, object?>()));
                insertCommand.Parameters.AddWithValue("$timeout_seconds", TimeoutValue(GetOptionalValue(channel, "timeout_seconds"), defaultTimeout));
                insertCommand.Parameters.AddWithValue("$retry_count", RetryCountValue(channel));
                insertCommand.Parameters.AddWithValue("$compat_json", JsonDumps(GetOptionalValue(channel, "compat") ?? new Dictionary<string, object?>()));
                insertCommand.Parameters.AddWithValue("$models_json", JsonDumps(GetOptionalValue(channel, "models") ?? new List<object?>()));
                insertCommand.Parameters.AddWithValue("$enabled", IsExplicitFalse(GetOptionalValue(channel, "enabled")) ? 0 : 1);
                insertCommand.Parameters.AddWithValue("$created_at", createdAt);
                insertCommand.Parameters.AddWithValue("$updated_at", now);
                insertCommand.ExecuteNonQuery();
                position++;
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static Dictionary<(string OwnerUsername, string Id), double> ReadExistingChannelCreatedTimes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_username, id, created_at FROM channels";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string OwnerUsername, string Id), double>();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetDouble(2);
        }

        return result;
    }
}
