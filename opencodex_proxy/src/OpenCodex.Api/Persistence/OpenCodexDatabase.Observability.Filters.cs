using Microsoft.Data.Sqlite;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static readonly IReadOnlyList<string> RequestLogMetadataColumns =
    [
        "id",
        "request_id",
        "created_at",
        "method",
        "path",
        "client_ip",
        "model",
        "upstream_model",
        "channel_id",
        "is_stream",
        "ttft_ms",
        "duration_ms",
        "status_code",
        "input_tokens",
        "cached_tokens",
        "output_tokens",
        "cost",
        "owner_username",
        "api_key_id",
        "error"
    ];

    private static readonly IReadOnlyList<string> RequestLogDetailColumns =
    [
        "request_headers",
        "request_body",
        "upstream_request_body",
        "upstream_response_body",
        "response_body",
        "web_search_json"
    ];

    private static readonly IReadOnlyList<string> TextLogFilterFields =
    [
        "request_id",
        "model",
        "upstream_model",
        "channel_id",
        "owner_username",
        "path",
        "client_ip",
        "error"
    ];

    private static readonly IReadOnlyList<string> IntegerLogFilterFields =
    [
        "status_code",
        "is_stream",
        "api_key_id"
    ];

    private static readonly HashSet<string> RequestStatusValues = new(StringComparer.Ordinal)
    {
        "success",
        "failed"
    };

    private static readonly IReadOnlyDictionary<string, (string OptionKey, string OptionType)> LogFilterFields =
        new Dictionary<string, (string OptionKey, string OptionType)>(StringComparer.Ordinal)
        {
            ["request_id"] = ("request_ids", "text"),
            ["model"] = ("models", "text"),
            ["upstream_model"] = ("upstream_models", "text"),
            ["channel_id"] = ("channel_ids", "text"),
            ["owner_username"] = ("owner_usernames", "text"),
            ["path"] = ("paths", "text"),
            ["status_code"] = ("status_codes", "int"),
            ["api_key_id"] = ("api_key_ids", "int")
        };

    private sealed record SqlQueryParameter(string Name, object Value);

    private sealed record LogWhereClause(
        string Clause,
        IReadOnlyList<SqlQueryParameter> Parameters);

    private static string LogMetadataSelectColumns(string? alias = null)
    {
        var prefix = alias is null ? string.Empty : $"{alias}.";
        return string.Join(", ", RequestLogMetadataColumns.Select(column => $"{prefix}{column} AS {column}"));
    }

    private static string LogDetailSelectColumns(string? alias = null)
    {
        var prefix = alias is null ? string.Empty : $"{alias}.";
        return string.Join(", ", RequestLogDetailColumns.Select(column => $"{prefix}{column} AS {column}"));
    }

    private static LogWhereClause BuildLogWhereClause(
        IReadOnlyDictionary<string, object?> filters,
        string? tableAlias = null)
    {
        var conditions = new List<string>();
        var parameters = new List<SqlQueryParameter>();
        var prefix = tableAlias is null ? string.Empty : $"{tableAlias}.";

        foreach (var field in TextLogFilterFields)
        {
            var value = GetOptionalValue(filters, field);
            if (IsEmptyLogFilterValue(value))
            {
                continue;
            }

            var parameterName = $"$p{parameters.Count}";
            conditions.Add($"{prefix}{field} LIKE {parameterName}");
            parameters.Add(new SqlQueryParameter(parameterName, $"%{value}%"));
        }

        foreach (var field in IntegerLogFilterFields)
        {
            var value = GetOptionalValue(filters, field);
            if (IsEmptyLogFilterValue(value) || !TryConvertInt64(value, out var parsed))
            {
                continue;
            }

            var parameterName = $"$p{parameters.Count}";
            conditions.Add($"{prefix}{field} = {parameterName}");
            parameters.Add(new SqlQueryParameter(parameterName, parsed));
        }

        var requestStatus = (GetOptionalValue(filters, "request_status")?.ToString() ?? string.Empty).Trim();
        if (RequestStatusValues.Contains(requestStatus))
        {
            conditions.Add(requestStatus == "success"
                ? $"({prefix}status_code < 400 AND ({prefix}error IS NULL OR {prefix}error = ''))"
                : $"({prefix}status_code >= 400 OR ({prefix}error IS NOT NULL AND {prefix}error != ''))");
        }

        foreach (var (field, op) in new[] { ("created_from", ">="), ("created_to", "<=") })
        {
            var value = GetOptionalValue(filters, field);
            if (IsEmptyLogFilterValue(value) || !TryConvertDouble(value, out var parsed))
            {
                continue;
            }

            var parameterName = $"$p{parameters.Count}";
            conditions.Add($"{prefix}created_at {op} {parameterName}");
            parameters.Add(new SqlQueryParameter(parameterName, parsed));
        }

        var clause = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
        return new LogWhereClause(clause, parameters);
    }

    private static Dictionary<string, object> EmptyLogFilterOptions()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["request_ids"] = new List<string>(),
            ["models"] = new List<string>(),
            ["upstream_models"] = new List<string>(),
            ["channel_ids"] = new List<string>(),
            ["owner_usernames"] = new List<string>(),
            ["paths"] = new List<string>(),
            ["status_codes"] = new List<long>(),
            ["api_key_ids"] = new List<long>(),
            ["request_statuses"] = new List<string> { "success", "failed" }
        };
    }

    private static List<string> DistinctTextValues(
        SqliteConnection connection,
        string field,
        LogWhereClause whereClause,
        object? query = null)
    {
        var parameters = whereClause.Parameters.ToList();
        var scopedWhere = AppendWhereCondition(whereClause.Clause, $"{field} IS NOT NULL AND {field} != ''");
        var queryText = (query?.ToString() ?? string.Empty).Trim();
        if (queryText.Length > 0)
        {
            var parameterName = $"$p{parameters.Count}";
            scopedWhere = AppendWhereCondition(scopedWhere, $"{field} LIKE {parameterName}");
            parameters.Add(new SqlQueryParameter(parameterName, $"%{queryText}%"));
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {field}
            FROM request_logs
            {scopedWhere}
            ORDER BY {field} ASC
            LIMIT 200
            """;
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static List<long> DistinctIntValues(
        SqliteConnection connection,
        string field,
        LogWhereClause whereClause,
        object? query = null)
    {
        var parameters = whereClause.Parameters.ToList();
        var scopedWhere = AppendWhereCondition(whereClause.Clause, $"{field} IS NOT NULL");
        var queryText = (query?.ToString() ?? string.Empty).Trim();
        if (queryText.Length > 0)
        {
            if (!TryConvertInt64(queryText, out var parsedQuery))
            {
                return [];
            }

            var parameterName = $"$p{parameters.Count}";
            scopedWhere = AppendWhereCondition(scopedWhere, $"{field} = {parameterName}");
            parameters.Add(new SqlQueryParameter(parameterName, parsedQuery));
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {field}
            FROM request_logs
            {scopedWhere}
            ORDER BY {field} ASC
            LIMIT 200
            """;
        AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetInt64(0));
            }
        }

        return values;
    }

    private static string AppendWhereCondition(string whereClause, string condition)
    {
        return whereClause.Length == 0
            ? $"WHERE {condition}"
            : $"{whereClause} AND {condition}";
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<SqlQueryParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static bool IsEmptyLogFilterValue(object? value)
    {
        return value is null || value is "";
    }

    private static int ParseLogPage(object? page)
    {
        return TryConvertInt32(page, out var parsed)
            ? Math.Max(1, parsed)
            : 1;
    }

    private static int ParseLogPageSize(object? pageSize)
    {
        return TryConvertInt32(pageSize, out var parsed)
            ? Math.Clamp(parsed, 1, 200)
            : 50;
    }
}
