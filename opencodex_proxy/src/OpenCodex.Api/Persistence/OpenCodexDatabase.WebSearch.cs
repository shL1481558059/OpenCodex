using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private const int DefaultWebSearchKeyUsageLimit = 1000;

    private static readonly HashSet<string> WebSearchProviders = new(StringComparer.Ordinal)
    {
        "tavily"
    };

    internal static IReadOnlyList<string> GetSupportedWebSearchProviders()
    {
        return WebSearchProviders.Order(StringComparer.Ordinal).ToList();
    }

    internal static int GetDefaultWebSearchKeyUsageLimit()
    {
        return DefaultWebSearchKeyUsageLimit;
    }

    public static WebSearchConfigRecord ReadWebSearchConfig(string dbPath)
    {
        Initialize(dbPath);
        using var connection = OpenConnection(dbPath);
        var enabled = false;
        using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.CommandText = "SELECT enabled FROM web_search_settings WHERE id = 1";
            using var reader = settingsCommand.ExecuteReader();
            if (reader.Read())
            {
                enabled = reader.GetInt32(0) != 0;
            }
        }

        using var keysCommand = connection.CreateCommand();
        keysCommand.CommandText = """
            SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
            FROM tavily_keys
            ORDER BY position ASC, id ASC
            """;
        using var keysReader = keysCommand.ExecuteReader();
        var keys = new List<TavilyKeyRecord>();
        while (keysReader.Read())
        {
            keys.Add(ReadTavilyKey(keysReader));
        }

        return new WebSearchConfigRecord(
            enabled,
            WebSearchProviders.Order(StringComparer.Ordinal).ToList(),
            DefaultWebSearchKeyUsageLimit,
            keys);
    }

    public static WebSearchConfigRecord ReplaceWebSearchConfig(
        string dbPath,
        IReadOnlyDictionary<string, object?> config)
    {
        Initialize(dbPath);
        var keysValue = GetOptionalValue(config, "keys") ?? new List<object?>();
        if (!TryAsList(keysValue, out var keys))
        {
            throw new ArgumentException("web search keys must be a list", nameof(config));
        }

        using var connection = OpenConnection(dbPath);
        var now = UnixTimeSeconds();
        var currentDefaultKeyUsageLimit = ReadCurrentWebSearchDefaultUsageLimit(connection);
        var defaultKeyUsageLimit = ParseRequiredPositiveInt(
            GetOptionalValue(config, "key_usage_limit") ?? currentDefaultKeyUsageLimit,
            "web search key_usage_limit");
        var existing = ReadExistingTavilyKeys(connection, now);
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var settingsCommand = connection.CreateCommand())
            {
                settingsCommand.Transaction = transaction;
                settingsCommand.CommandText = """
                    INSERT INTO web_search_settings (id, enabled, key_usage_limit, created_at, updated_at)
                    VALUES (1, $enabled, $key_usage_limit, $created_at, $updated_at)
                    ON CONFLICT(id) DO UPDATE SET
                        enabled = excluded.enabled,
                        key_usage_limit = excluded.key_usage_limit,
                        updated_at = excluded.updated_at
                    """;
                settingsCommand.Parameters.AddWithValue("$enabled", GetOptionalValue(config, "enabled") is true ? 1 : 0);
                settingsCommand.Parameters.AddWithValue("$key_usage_limit", defaultKeyUsageLimit);
                settingsCommand.Parameters.AddWithValue("$created_at", now);
                settingsCommand.Parameters.AddWithValue("$updated_at", now);
                settingsCommand.ExecuteNonQuery();
            }

            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM tavily_keys";
                deleteCommand.ExecuteNonQuery();
            }

            for (var position = 0; position < keys.Count; position++)
            {
                if (!TryAsObject(keys[position], out var item))
                {
                    throw new ArgumentException($"web search keys[{position + 1}] must be an object", nameof(config));
                }

                var provider = NormalizeWebSearchProvider(GetOptionalValue(item, "provider"));
                var apiKey = WebSearchApiKey(item);
                if (apiKey.Length == 0)
                {
                    throw new ArgumentException($"web search keys[{position + 1}].key is required", nameof(config));
                }

                var existingId = ParsePositiveLong(GetOptionalValue(item, "id"));
                var old = existingId is null ? null : existing.GetValueOrDefault(existingId.Value);
                var usageLimitSource = GetOptionalValue(item, "usage_limit") ?? GetOptionalValue(item, "key_usage_limit");
                var sameKey = old is not null && old.ApiKey == apiKey && old.Provider == provider;
                int usageLimit;
                if (usageLimitSource is null && sameKey)
                {
                    usageLimit = old!.UsageLimit;
                }
                else
                {
                    usageLimit = ParseRequiredPositiveInt(
                        usageLimitSource ?? defaultKeyUsageLimit,
                        $"web search keys[{position + 1}].usage_limit");
                }

                int usageCount;
                double createdAt;
                if (item.ContainsKey("usage_count"))
                {
                    usageCount = ParseRequiredNonNegativeInt(
                        GetOptionalValue(item, "usage_count"),
                        $"web search keys[{position + 1}].usage_count");
                    createdAt = sameKey ? old!.CreatedAt : now;
                }
                else if (sameKey)
                {
                    usageCount = old!.UsageCount;
                    createdAt = old.CreatedAt;
                }
                else
                {
                    usageCount = 0;
                    createdAt = now;
                }

                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO tavily_keys (
                        id, position, provider, api_key, enabled, usage_count,
                        usage_limit, created_at, updated_at
                    ) VALUES (
                        $id, $position, $provider, $api_key, $enabled, $usage_count,
                        $usage_limit, $created_at, $updated_at
                    )
                    """;
                AddNullableInt64(insertCommand, "$id", existingId);
                insertCommand.Parameters.AddWithValue("$position", position);
                insertCommand.Parameters.AddWithValue("$provider", provider);
                insertCommand.Parameters.AddWithValue("$api_key", apiKey);
                insertCommand.Parameters.AddWithValue("$enabled", IsExplicitFalse(GetOptionalValue(item, "enabled")) ? 0 : 1);
                insertCommand.Parameters.AddWithValue("$usage_count", usageCount);
                insertCommand.Parameters.AddWithValue("$usage_limit", usageLimit);
                insertCommand.Parameters.AddWithValue("$created_at", createdAt);
                insertCommand.Parameters.AddWithValue("$updated_at", now);
                insertCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return ReadWebSearchConfig(dbPath);
    }

    public static TavilyKeyRecord? ReserveTavilyKey(string dbPath)
    {
        return ReserveTavilyKey(dbPath, keyId: null, allowDisabled: false);
    }

    public static TavilyKeyRecord? ReserveTavilyKeyById(string dbPath, long keyId)
    {
        return ReserveTavilyKey(dbPath, keyId, allowDisabled: true);
    }

    private static TavilyKeyRecord? ReserveTavilyKey(
        string dbPath,
        long? keyId,
        bool allowDisabled)
    {
        Initialize(dbPath);
        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            if (keyId is null)
            {
                selectCommand.CommandText = """
                    SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
                    FROM tavily_keys
                    WHERE enabled = 1
                      AND usage_count < usage_limit
                    ORDER BY position ASC, id ASC
                    LIMIT 1
                    """;
            }
            else
            {
                selectCommand.CommandText = allowDisabled
                    ? """
                      SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
                      FROM tavily_keys
                      WHERE id = $id
                        AND usage_count < usage_limit
                      """
                    : """
                      SELECT id, position, provider, api_key, enabled, usage_count, usage_limit
                      FROM tavily_keys
                      WHERE id = $id
                        AND enabled = 1
                        AND usage_count < usage_limit
                      """;
                selectCommand.Parameters.AddWithValue("$id", keyId.Value);
            }

            using var reader = selectCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return null;
            }

            var id = reader.GetInt64(reader.GetOrdinal("id"));
            var position = reader.GetInt32(reader.GetOrdinal("position"));
            var provider = reader.GetString(reader.GetOrdinal("provider"));
            var key = reader.GetString(reader.GetOrdinal("api_key"));
            var enabled = reader.GetInt32(reader.GetOrdinal("enabled")) != 0;
            var nextUsage = reader.GetInt32(reader.GetOrdinal("usage_count")) + 1;
            var usageLimit = reader.GetInt32(reader.GetOrdinal("usage_limit"));
            reader.Close();

            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE tavily_keys
                SET usage_count = $usage_count,
                    updated_at = $updated_at
                WHERE id = $id
                """;
            updateCommand.Parameters.AddWithValue("$usage_count", nextUsage);
            updateCommand.Parameters.AddWithValue("$updated_at", UnixTimeSeconds());
            updateCommand.Parameters.AddWithValue("$id", id);
            updateCommand.ExecuteNonQuery();
            transaction.Commit();

            return new TavilyKeyRecord(
                id,
                position,
                provider,
                key,
                enabled,
                nextUsage,
                usageLimit,
                usageLimit);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private sealed record ExistingTavilyKey(
        string Provider,
        string ApiKey,
        int UsageCount,
        int UsageLimit,
        double CreatedAt);

    private static int ReadCurrentWebSearchDefaultUsageLimit(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key_usage_limit FROM web_search_settings WHERE id = 1";
        var value = command.ExecuteScalar();
        return value is null || value is DBNull
            ? DefaultWebSearchKeyUsageLimit
            : Convert.ToInt32(value);
    }

    private static Dictionary<long, ExistingTavilyKey> ReadExistingTavilyKeys(SqliteConnection connection, double now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider, api_key, usage_count, usage_limit, created_at
            FROM tavily_keys
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, ExistingTavilyKey>();
        while (reader.Read())
        {
            result[reader.GetInt64(reader.GetOrdinal("id"))] = new ExistingTavilyKey(
                reader.GetString(reader.GetOrdinal("provider")),
                reader.GetString(reader.GetOrdinal("api_key")),
                reader.IsDBNull(reader.GetOrdinal("usage_count"))
                    ? 0
                    : reader.GetInt32(reader.GetOrdinal("usage_count")),
                reader.IsDBNull(reader.GetOrdinal("usage_limit"))
                    ? DefaultWebSearchKeyUsageLimit
                    : reader.GetInt32(reader.GetOrdinal("usage_limit")),
                reader.IsDBNull(reader.GetOrdinal("created_at"))
                    ? now
                    : reader.GetDouble(reader.GetOrdinal("created_at")));
        }

        return result;
    }
}
