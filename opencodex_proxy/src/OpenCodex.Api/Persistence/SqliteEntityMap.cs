using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

internal static class SqliteEntityMap<TEntity>
    where TEntity : BaseEntity
{
    public static TEntity? GetById(SqliteConnection connection, object id)
    {
        if (typeof(TEntity) == typeof(UserEntity))
        {
            return (TEntity?)(object?)GetUserById(connection, id);
        }

        if (typeof(TEntity) == typeof(AccessApiKeyEntity))
        {
            return (TEntity?)(object?)GetAccessApiKeyById(connection, id);
        }

        if (typeof(TEntity) == typeof(WebSearchSettingsEntity))
        {
            return (TEntity?)(object?)GetWebSearchSettingsById(connection, id);
        }

        if (typeof(TEntity) == typeof(TavilyKeyEntity))
        {
            return (TEntity?)(object?)GetTavilyKeyById(connection, id);
        }

        if (typeof(TEntity) == typeof(ChannelEntity))
        {
            return (TEntity?)(object?)GetChannelById(connection, id);
        }

        throw new NotSupportedException(
            $"No SQLite entity map is registered for {typeof(TEntity).Name}.");
    }

    public static IReadOnlyList<TEntity> ListAll(SqliteConnection connection)
    {
        if (typeof(TEntity) == typeof(UserEntity))
        {
            return ListUsers(connection).Cast<TEntity>().ToList();
        }

        if (typeof(TEntity) == typeof(AccessApiKeyEntity))
        {
            return ListAccessApiKeys(connection).Cast<TEntity>().ToList();
        }

        if (typeof(TEntity) == typeof(WebSearchSettingsEntity))
        {
            return ListWebSearchSettings(connection).Cast<TEntity>().ToList();
        }

        if (typeof(TEntity) == typeof(TavilyKeyEntity))
        {
            return ListTavilyKeys(connection).Cast<TEntity>().ToList();
        }

        if (typeof(TEntity) == typeof(ChannelEntity))
        {
            return ListChannels(connection).Cast<TEntity>().ToList();
        }

        throw new NotSupportedException(
            $"No SQLite entity map is registered for {typeof(TEntity).Name}.");
    }

    private static UserEntity? GetUserById(SqliteConnection connection, object id)
    {
        var username = OpenCodexDatabase.NormalizeRepositoryUsername(id);
        if (username.Length == 0)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, password_hash, role, enabled, created_at, updated_at
            FROM users
            WHERE username = $username
            """;
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new UserEntity(
                reader.GetString(reader.GetOrdinal("username")),
                reader.GetString(reader.GetOrdinal("password_hash")),
                reader.GetString(reader.GetOrdinal("role")),
                reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
                reader.GetDouble(reader.GetOrdinal("created_at")),
                reader.GetDouble(reader.GetOrdinal("updated_at")))
            : null;
    }

    private static IReadOnlyList<UserEntity> ListUsers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT username, password_hash, role, enabled, created_at, updated_at
            FROM users
            ORDER BY role ASC, username ASC
            """;
        using var reader = command.ExecuteReader();
        var users = new List<UserEntity>();
        while (reader.Read())
        {
            users.Add(new UserEntity(
                reader.GetString(reader.GetOrdinal("username")),
                reader.GetString(reader.GetOrdinal("password_hash")),
                reader.GetString(reader.GetOrdinal("role")),
                reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
                reader.GetDouble(reader.GetOrdinal("created_at")),
                reader.GetDouble(reader.GetOrdinal("updated_at"))));
        }

        return users;
    }

    private static ChannelEntity? GetChannelById(SqliteConnection connection, object id)
    {
        if (!TryNormalizeChannelId(id, out var ownerUsername, out var channelId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                   headers_json, timeout_seconds, retry_count, compat_json, models_json,
                   enabled, created_at, updated_at
            FROM channels
            WHERE owner_username = $owner_username
              AND id = $id
            """;
        command.Parameters.AddWithValue("$owner_username", ownerUsername);
        command.Parameters.AddWithValue("$id", channelId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadChannel(reader) : null;
    }

    private static IReadOnlyList<ChannelEntity> ListChannels(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_username, id, position, name, type, baseurl, apikey, auth_mode,
                   headers_json, timeout_seconds, retry_count, compat_json, models_json,
                   enabled, created_at, updated_at
            FROM channels
            ORDER BY owner_username ASC, position ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        var channels = new List<ChannelEntity>();
        while (reader.Read())
        {
            channels.Add(ReadChannel(reader));
        }

        return channels;
    }

    private static ChannelEntity ReadChannel(SqliteDataReader reader)
    {
        return new ChannelEntity(
            reader.GetString(reader.GetOrdinal("owner_username")),
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("position")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("type")),
            reader.GetString(reader.GetOrdinal("baseurl")),
            reader.GetString(reader.GetOrdinal("apikey")),
            reader.GetString(reader.GetOrdinal("auth_mode")),
            OpenCodexDatabase.ParseRepositoryJsonObject(reader.GetString(reader.GetOrdinal("headers_json"))),
            reader.GetInt32(reader.GetOrdinal("timeout_seconds")),
            reader.GetInt32(reader.GetOrdinal("retry_count")),
            OpenCodexDatabase.ParseRepositoryJsonObject(reader.GetString(reader.GetOrdinal("compat_json"))),
            OpenCodexDatabase.ParseRepositoryJsonList(reader.GetString(reader.GetOrdinal("models_json"))),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")));
    }

    private static AccessApiKeyEntity? GetAccessApiKeyById(SqliteConnection connection, object id)
    {
        if (!TryNormalizeLongId(id, out var keyId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_username, name, key_hash, key_plaintext, key_prefix, key_suffix,
                   enabled, created_at, updated_at, last_used_at
            FROM access_api_keys
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", keyId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccessApiKey(reader) : null;
    }

    private static IReadOnlyList<AccessApiKeyEntity> ListAccessApiKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_username, name, key_hash, key_plaintext, key_prefix, key_suffix,
                   enabled, created_at, updated_at, last_used_at
            FROM access_api_keys
            ORDER BY owner_username ASC, id DESC
            """;
        using var reader = command.ExecuteReader();
        var keys = new List<AccessApiKeyEntity>();
        while (reader.Read())
        {
            keys.Add(ReadAccessApiKey(reader));
        }

        return keys;
    }

    private static WebSearchSettingsEntity? GetWebSearchSettingsById(SqliteConnection connection, object id)
    {
        if (!TryNormalizeLongId(id, out var settingsId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, enabled, key_usage_limit, created_at, updated_at
            FROM web_search_settings
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", settingsId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWebSearchSettings(reader) : null;
    }

    private static IReadOnlyList<WebSearchSettingsEntity> ListWebSearchSettings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, enabled, key_usage_limit, created_at, updated_at
            FROM web_search_settings
            ORDER BY id ASC
            """;
        using var reader = command.ExecuteReader();
        var items = new List<WebSearchSettingsEntity>();
        while (reader.Read())
        {
            items.Add(ReadWebSearchSettings(reader));
        }

        return items;
    }

    private static WebSearchSettingsEntity ReadWebSearchSettings(SqliteDataReader reader)
    {
        return new WebSearchSettingsEntity(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetInt32(reader.GetOrdinal("key_usage_limit")),
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")));
    }

    private static TavilyKeyEntity? GetTavilyKeyById(SqliteConnection connection, object id)
    {
        if (!TryNormalizeLongId(id, out var keyId))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, position, provider, api_key, enabled, usage_count, usage_limit,
                   created_at, updated_at
            FROM tavily_keys
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", keyId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTavilyKeyEntity(reader) : null;
    }

    private static IReadOnlyList<TavilyKeyEntity> ListTavilyKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, position, provider, api_key, enabled, usage_count, usage_limit,
                   created_at, updated_at
            FROM tavily_keys
            ORDER BY position ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        var keys = new List<TavilyKeyEntity>();
        while (reader.Read())
        {
            keys.Add(ReadTavilyKeyEntity(reader));
        }

        return keys;
    }

    private static TavilyKeyEntity ReadTavilyKeyEntity(SqliteDataReader reader)
    {
        return new TavilyKeyEntity(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("position")),
            reader.GetString(reader.GetOrdinal("provider")),
            reader.GetString(reader.GetOrdinal("api_key")),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetInt32(reader.GetOrdinal("usage_count")),
            reader.GetInt32(reader.GetOrdinal("usage_limit")),
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")));
    }

    private static AccessApiKeyEntity ReadAccessApiKey(SqliteDataReader reader)
    {
        return new AccessApiKeyEntity(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("owner_username")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("key_hash")),
            GetNullableString(reader, "key_plaintext"),
            reader.GetString(reader.GetOrdinal("key_prefix")),
            reader.GetString(reader.GetOrdinal("key_suffix")),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")),
            GetNullableDouble(reader, "last_used_at"));
    }

    private static bool TryNormalizeLongId(object id, out long normalizedId)
    {
        switch (id)
        {
            case long longId:
                normalizedId = longId;
                return longId > 0;
            case int intId:
                normalizedId = intId;
                return intId > 0;
            case string text when long.TryParse(text.Trim(), out var parsed):
                normalizedId = parsed;
                return parsed > 0;
            default:
            normalizedId = 0;
            return false;
        }
    }

    private static bool TryNormalizeChannelId(
        object id,
        out string ownerUsername,
        out string channelId)
    {
        switch (id)
        {
            case ValueTuple<string, string> tuple:
                ownerUsername = OpenCodexDatabase.NormalizeRepositoryUsername(tuple.Item1);
                channelId = NormalizeStringId(tuple.Item2);
                break;
            case Tuple<string, string> tuple:
                ownerUsername = OpenCodexDatabase.NormalizeRepositoryUsername(tuple.Item1);
                channelId = NormalizeStringId(tuple.Item2);
                break;
            default:
                ownerUsername = string.Empty;
                channelId = string.Empty;
                return false;
        }

        return ownerUsername.Length > 0 && channelId.Length > 0;
    }

    private static string NormalizeStringId(object? value)
    {
        return value?.ToString()?.Trim() ?? string.Empty;
    }

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? GetNullableDouble(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
