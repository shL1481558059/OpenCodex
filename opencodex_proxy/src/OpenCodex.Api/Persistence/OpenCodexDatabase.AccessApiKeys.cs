using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    public static AccessApiKeyRecord CreateAccessApiKey(
        string dbPath,
        string ownerUsername,
        string name = "")
    {
        Initialize(dbPath, ownerUsername);
        ownerUsername = NormalizeUsername(ownerUsername);
        if (ownerUsername.Length == 0)
        {
            throw new ArgumentException("owner_username is required", nameof(ownerUsername));
        }

        if (GetUser(dbPath, ownerUsername) is null)
        {
            throw new InvalidOperationException("user not found");
        }

        var rawKey = GenerateAccessApiKey();
        var now = UnixTimeSeconds();
        long keyId;
        using (var connection = OpenConnection(dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO access_api_keys (
                    owner_username, name, key_hash, key_plaintext, key_prefix, key_suffix,
                    enabled, created_at, updated_at
                ) VALUES (
                    $owner_username, $name, $key_hash, $key_plaintext, $key_prefix,
                    $key_suffix, 1, $created_at, $updated_at
                )
                """;
            command.Parameters.AddWithValue("$owner_username", ownerUsername);
            command.Parameters.AddWithValue("$name", (name ?? string.Empty).Trim());
            command.Parameters.AddWithValue("$key_hash", HashAccessApiKey(rawKey));
            command.Parameters.AddWithValue("$key_plaintext", rawKey);
            command.Parameters.AddWithValue("$key_prefix", rawKey[..12]);
            command.Parameters.AddWithValue("$key_suffix", rawKey[^6..]);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            command.ExecuteNonQuery();
            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            keyId = (long)(idCommand.ExecuteScalar() ?? 0L);
        }

        var metadata = GetAccessApiKey(dbPath, keyId) ?? throw new InvalidOperationException("failed to create access api key");
        return metadata with { Key = rawKey };
    }

    public static IReadOnlyList<AccessApiKeyRecord> ListAccessApiKeys(
        string dbPath,
        string? ownerUsername = null)
    {
        Initialize(dbPath, ownerUsername ?? "admin");
        ownerUsername = ownerUsername is null ? null : NormalizeUsername(ownerUsername);

        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            command.CommandText = """
                SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix,
                       enabled, created_at, updated_at, last_used_at
                FROM access_api_keys
                WHERE owner_username = $owner_username
                ORDER BY id DESC
                """;
            command.Parameters.AddWithValue("$owner_username", ownerUsername);
        }
        else
        {
            command.CommandText = """
                SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix,
                       enabled, created_at, updated_at, last_used_at
                FROM access_api_keys
                ORDER BY owner_username ASC, id DESC
                """;
        }

        using var reader = command.ExecuteReader();
        var keys = new List<AccessApiKeyRecord>();
        while (reader.Read())
        {
            keys.Add(ReadAccessApiKey(reader, includePlaintext: true));
        }

        return keys;
    }

    public static AccessApiKeyRecord? GetAccessApiKey(string dbPath, long keyId)
    {
        Initialize(dbPath);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_username, name, key_plaintext, key_prefix, key_suffix,
                   enabled, created_at, updated_at, last_used_at
            FROM access_api_keys
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", keyId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccessApiKey(reader, includePlaintext: true) : null;
    }

    public static AccessApiKeyRecord SetAccessApiKeyEnabled(
        string dbPath,
        long keyId,
        bool enabled,
        string? ownerUsername = null)
    {
        Initialize(dbPath, ownerUsername ?? "admin");
        ownerUsername = ownerUsername is null ? null : NormalizeUsername(ownerUsername);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            command.CommandText = """
                UPDATE access_api_keys
                SET enabled = $enabled,
                    updated_at = $updated_at
                WHERE id = $id
                  AND owner_username = $owner_username
                """;
            command.Parameters.AddWithValue("$owner_username", ownerUsername);
        }
        else
        {
            command.CommandText = """
                UPDATE access_api_keys
                SET enabled = $enabled,
                    updated_at = $updated_at
                WHERE id = $id
                """;
        }

        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", UnixTimeSeconds());
        command.Parameters.AddWithValue("$id", keyId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException("api key not found");
        }

        return GetAccessApiKey(dbPath, keyId) ?? throw new InvalidOperationException("failed to update api key");
    }

    public static void DeleteAccessApiKey(
        string dbPath,
        long keyId,
        string? ownerUsername = null)
    {
        Initialize(dbPath, ownerUsername ?? "admin");
        ownerUsername = ownerUsername is null ? null : NormalizeUsername(ownerUsername);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            command.CommandText = "DELETE FROM access_api_keys WHERE id = $id AND owner_username = $owner_username";
            command.Parameters.AddWithValue("$owner_username", ownerUsername);
        }
        else
        {
            command.CommandText = "DELETE FROM access_api_keys WHERE id = $id";
        }

        command.Parameters.AddWithValue("$id", keyId);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException("api key not found");
        }
    }

    public static AuthenticatedAccessApiKeyRecord? AuthenticateAccessApiKey(string dbPath, string? rawKey)
    {
        rawKey = (rawKey ?? string.Empty).Trim();
        if (rawKey.Length == 0)
        {
            return null;
        }

        Initialize(dbPath);
        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                SELECT
                    k.id,
                    k.owner_username,
                    k.name,
                    k.key_prefix,
                    k.key_suffix,
                    k.enabled,
                    k.created_at,
                    k.updated_at,
                    k.last_used_at,
                    u.role AS user_role,
                    u.enabled AS user_enabled
                FROM access_api_keys k
                JOIN users u ON u.username = k.owner_username
                WHERE k.key_hash = $key_hash
                """;
            selectCommand.Parameters.AddWithValue("$key_hash", HashAccessApiKey(rawKey));
            using var reader = selectCommand.ExecuteReader();
            if (!reader.Read()
                || reader.GetInt32(reader.GetOrdinal("enabled")) == 0
                || reader.GetInt32(reader.GetOrdinal("user_enabled")) == 0)
            {
                transaction.Rollback();
                return null;
            }

            var keyId = reader.GetInt64(reader.GetOrdinal("id"));
            var owner = reader.GetString(reader.GetOrdinal("owner_username"));
            var name = reader.GetString(reader.GetOrdinal("name"));
            var keyPrefix = reader.GetString(reader.GetOrdinal("key_prefix"));
            var keySuffix = reader.GetString(reader.GetOrdinal("key_suffix"));
            var enabled = reader.GetInt32(reader.GetOrdinal("enabled")) != 0;
            var createdAt = reader.GetDouble(reader.GetOrdinal("created_at"));
            var userRole = reader.GetString(reader.GetOrdinal("user_role"));
            var userEnabled = reader.GetInt32(reader.GetOrdinal("user_enabled")) != 0;
            reader.Close();

            var now = UnixTimeSeconds();
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE access_api_keys
                SET last_used_at = $last_used_at,
                    updated_at = $updated_at
                WHERE id = $id
                """;
            updateCommand.Parameters.AddWithValue("$last_used_at", now);
            updateCommand.Parameters.AddWithValue("$updated_at", now);
            updateCommand.Parameters.AddWithValue("$id", keyId);
            updateCommand.ExecuteNonQuery();
            transaction.Commit();

            return new AuthenticatedAccessApiKeyRecord(
                keyId,
                owner,
                name,
                keyPrefix,
                keySuffix,
                $"{keyPrefix}...{keySuffix}",
                enabled,
                createdAt,
                now,
                now,
                new AccessApiKeyUserRecord(owner, userRole, userEnabled));
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
