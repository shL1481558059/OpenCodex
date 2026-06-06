using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static ChannelRecord ReadChannel(SqliteDataReader reader)
    {
        return new ChannelRecord(
            reader.GetString(reader.GetOrdinal("owner_username")),
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("name")),
            reader.GetString(reader.GetOrdinal("type")),
            reader.GetString(reader.GetOrdinal("baseurl")),
            reader.GetString(reader.GetOrdinal("apikey")),
            reader.GetString(reader.GetOrdinal("auth_mode")),
            ParseJsonObject(reader.GetString(reader.GetOrdinal("headers_json"))),
            reader.GetInt32(reader.GetOrdinal("timeout_seconds")),
            reader.GetInt32(reader.GetOrdinal("retry_count")),
            ParseJsonObject(reader.GetString(reader.GetOrdinal("compat_json"))),
            ParseJsonList(reader.GetString(reader.GetOrdinal("models_json"))),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0);
    }

    private static UserRecord ReadUser(SqliteDataReader reader)
    {
        return new UserRecord(
            reader.GetString(reader.GetOrdinal("username")),
            reader.GetString(reader.GetOrdinal("role")),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")));
    }

    private static AccessApiKeyRecord ReadAccessApiKey(SqliteDataReader reader, bool includePlaintext)
    {
        var keyPrefix = reader.GetString(reader.GetOrdinal("key_prefix"));
        var keySuffix = reader.GetString(reader.GetOrdinal("key_suffix"));
        return new AccessApiKeyRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("owner_username")),
            reader.GetString(reader.GetOrdinal("name")),
            keyPrefix,
            keySuffix,
            $"{keyPrefix}...{keySuffix}",
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.GetDouble(reader.GetOrdinal("created_at")),
            reader.GetDouble(reader.GetOrdinal("updated_at")),
            GetNullableDouble(reader, "last_used_at"),
            includePlaintext ? GetNullableString(reader, "key_plaintext") : null);
    }

    private static TavilyKeyRecord ReadTavilyKey(SqliteDataReader reader)
    {
        var usageLimit = reader.IsDBNull(reader.GetOrdinal("usage_limit"))
            ? DefaultWebSearchKeyUsageLimit
            : reader.GetInt32(reader.GetOrdinal("usage_limit"));
        return new TavilyKeyRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("position")),
            reader.GetString(reader.GetOrdinal("provider")),
            reader.GetString(reader.GetOrdinal("api_key")),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            reader.IsDBNull(reader.GetOrdinal("usage_count"))
                ? 0
                : reader.GetInt32(reader.GetOrdinal("usage_count")),
            usageLimit,
            usageLimit);
    }

    private static RequestLogRecord ReadRequestLog(SqliteDataReader reader)
    {
        var statusCode = GetNullableInt(reader, "status_code");
        var error = GetNullableString(reader, "error");
        return new RequestLogRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetNullableString(reader, "request_id"),
            GetNullableDouble(reader, "created_at"),
            GetNullableString(reader, "method"),
            GetNullableString(reader, "path"),
            GetNullableString(reader, "client_ip"),
            GetNullableString(reader, "model"),
            GetNullableString(reader, "upstream_model"),
            GetNullableString(reader, "channel_id"),
            GetNullableInt(reader, "is_stream") != 0,
            GetNullableInt(reader, "ttft_ms"),
            GetNullableInt(reader, "duration_ms"),
            statusCode,
            GetNullableInt(reader, "input_tokens") ?? 0,
            GetNullableInt(reader, "cached_tokens") ?? 0,
            GetNullableInt(reader, "output_tokens") ?? 0,
            GetNullableDouble(reader, "cost") ?? 0.0,
            GetNullableString(reader, "owner_username") ?? "admin",
            GetNullableLong(reader, "api_key_id"),
            error,
            GetNullableString(reader, "request_headers"),
            GetNullableString(reader, "request_body"),
            GetNullableString(reader, "upstream_request_body"),
            GetNullableString(reader, "upstream_response_body"),
            GetNullableString(reader, "response_body"),
            GetNullableString(reader, "web_search_json"),
            RequestStatus(statusCode, error));
    }

    private static RequestLogEventRecord ReadRequestLogEvent(SqliteDataReader reader)
    {
        var statusCode = GetNullableInt(reader, "status_code");
        var error = GetNullableString(reader, "error");
        return new RequestLogEventRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetNullableString(reader, "request_id"),
            GetNullableDouble(reader, "created_at"),
            GetNullableString(reader, "method"),
            GetNullableString(reader, "path"),
            GetNullableString(reader, "client_ip"),
            GetNullableString(reader, "model"),
            GetNullableString(reader, "upstream_model"),
            GetNullableString(reader, "channel_id"),
            GetNullableInt(reader, "is_stream") != 0,
            GetNullableInt(reader, "ttft_ms"),
            GetNullableInt(reader, "duration_ms"),
            statusCode,
            GetNullableInt(reader, "input_tokens") ?? 0,
            GetNullableInt(reader, "cached_tokens") ?? 0,
            GetNullableInt(reader, "output_tokens") ?? 0,
            GetNullableDouble(reader, "cost") ?? 0.0,
            GetNullableString(reader, "owner_username") ?? "admin",
            GetNullableLong(reader, "api_key_id"),
            error,
            RequestStatus(statusCode, error));
    }
}
