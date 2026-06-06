using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    public static UsageRecord ExtractUsage(IReadOnlyDictionary<string, object?> response, string protocol)
    {
        var usage = GetOptionalValue(response, "usage");
        if (!TryAsObject(usage, out var usageObject))
        {
            usageObject = [];
        }

        return protocol switch
        {
            "responses" => new UsageRecord(
                ToInt(GetOptionalValue(usageObject, "input_tokens")),
                CachedTokensFromNestedDetails(usageObject, "input_tokens_details"),
                ToInt(GetOptionalValue(usageObject, "output_tokens"))),
            "messages" => new UsageRecord(
                ToInt(GetOptionalValue(usageObject, "input_tokens")),
                ToInt(GetOptionalValue(usageObject, "cache_creation_input_tokens"))
                    + ToInt(GetOptionalValue(usageObject, "cache_read_input_tokens")),
                ToInt(GetOptionalValue(usageObject, "output_tokens"))),
            "chat" => new UsageRecord(
                ToInt(GetOptionalValue(usageObject, "prompt_tokens")),
                ChatCachedTokens(usageObject),
                ToInt(GetOptionalValue(usageObject, "completion_tokens"))),
            _ => new UsageRecord(0, 0, 0)
        };
    }

    public static long WriteRequestLog(
        string dbPath,
        IReadOnlyDictionary<string, object?> record,
        string defaultOwnerUsername = "admin")
    {
        defaultOwnerUsername = NormalizeUsername(defaultOwnerUsername);
        if (defaultOwnerUsername.Length == 0)
        {
            defaultOwnerUsername = "admin";
        }

        Initialize(dbPath, defaultOwnerUsername);
        using var connection = OpenConnection(dbPath);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var metadataCommand = connection.CreateCommand();
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText = """
                INSERT INTO request_logs (
                    request_id, created_at, method, path, client_ip,
                    model, upstream_model, channel_id, is_stream, ttft_ms,
                    duration_ms, status_code, input_tokens, cached_tokens,
                    output_tokens, cost, owner_username, api_key_id, error
                ) VALUES (
                    $request_id, $created_at, $method, $path, $client_ip,
                    $model, $upstream_model, $channel_id, $is_stream, $ttft_ms,
                    $duration_ms, $status_code, $input_tokens, $cached_tokens,
                    $output_tokens, $cost, $owner_username, $api_key_id, $error
                )
                """;
            AddNullableString(metadataCommand, "$request_id", OptionalNullableString(record, "request_id"));
            AddNullableDouble(metadataCommand, "$created_at", OptionalNullableDouble(record, "created_at"));
            AddNullableString(metadataCommand, "$method", OptionalNullableString(record, "method"));
            AddNullableString(metadataCommand, "$path", OptionalNullableString(record, "path"));
            AddNullableString(metadataCommand, "$client_ip", OptionalNullableString(record, "client_ip"));
            AddNullableString(metadataCommand, "$model", OptionalNullableString(record, "model"));
            AddNullableString(metadataCommand, "$upstream_model", OptionalNullableString(record, "upstream_model"));
            AddNullableString(metadataCommand, "$channel_id", OptionalNullableString(record, "channel_id"));
            metadataCommand.Parameters.AddWithValue("$is_stream", OptionalInt(record, "is_stream", 0));
            AddNullableInt32(metadataCommand, "$ttft_ms", OptionalNullableInt(record, "ttft_ms"));
            AddNullableInt32(metadataCommand, "$duration_ms", OptionalNullableInt(record, "duration_ms"));
            AddNullableInt32(metadataCommand, "$status_code", OptionalNullableInt(record, "status_code"));
            metadataCommand.Parameters.AddWithValue("$input_tokens", OptionalInt(record, "input_tokens", 0));
            metadataCommand.Parameters.AddWithValue("$cached_tokens", OptionalInt(record, "cached_tokens", 0));
            metadataCommand.Parameters.AddWithValue("$output_tokens", OptionalInt(record, "output_tokens", 0));
            metadataCommand.Parameters.AddWithValue("$cost", OptionalDouble(record, "cost", 0.0));
            metadataCommand.Parameters.AddWithValue("$owner_username", OptionalNullableString(record, "owner_username") ?? defaultOwnerUsername);
            AddNullableInt64(metadataCommand, "$api_key_id", OptionalNullableLong(record, "api_key_id"));
            AddNullableString(metadataCommand, "$error", OptionalNullableString(record, "error"));
            metadataCommand.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var logId = (long)(idCommand.ExecuteScalar() ?? 0L);

            using var detailCommand = connection.CreateCommand();
            detailCommand.Transaction = transaction;
            detailCommand.CommandText = """
                INSERT INTO request_log_details (
                    log_id, request_headers, request_body, upstream_request_body,
                    upstream_response_body, response_body, web_search_json
                ) VALUES (
                    $log_id, $request_headers, $request_body, $upstream_request_body,
                    $upstream_response_body, $response_body, $web_search_json
                )
                """;
            detailCommand.Parameters.AddWithValue("$log_id", logId);
            AddNullableString(detailCommand, "$request_headers", OptionalNullableString(record, "request_headers"));
            AddNullableString(detailCommand, "$request_body", OptionalNullableString(record, "request_body"));
            AddNullableString(detailCommand, "$upstream_request_body", OptionalNullableString(record, "upstream_request_body"));
            AddNullableString(detailCommand, "$upstream_response_body", OptionalNullableString(record, "upstream_response_body"));
            AddNullableString(detailCommand, "$response_body", OptionalNullableString(record, "response_body"));
            AddNullableString(detailCommand, "$web_search_json", OptionalNullableString(record, "web_search_json"));
            detailCommand.ExecuteNonQuery();

            transaction.Commit();
            return logId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
