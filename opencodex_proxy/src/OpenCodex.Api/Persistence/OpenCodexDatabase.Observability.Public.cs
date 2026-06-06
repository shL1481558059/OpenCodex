using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    public static IReadOnlyList<RequestLogRecord> ReadLogs(
        string dbPath,
        int limit = 200,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (!File.Exists(dbPath))
        {
            return [];
        }

        var parsedLimit = Math.Clamp(limit, 1, 1000);
        var whereClause = BuildLogWhereClause(filters ?? new Dictionary<string, object?>(), tableAlias: "l");
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {LogMetadataSelectColumns("l")},
                {LogDetailSelectColumns("d")}
            FROM request_logs l
            LEFT JOIN request_log_details d ON d.log_id = l.id
            {whereClause.Clause}
            ORDER BY l.id DESC
            LIMIT $limit
            """;
        AddParameters(command, whereClause.Parameters);
        command.Parameters.AddWithValue("$limit", parsedLimit);
        using var reader = command.ExecuteReader();
        var logs = new List<RequestLogRecord>();
        while (reader.Read())
        {
            logs.Add(ReadRequestLog(reader));
        }

        return logs;
    }

    public static RequestLogPageRecord ReadLogsPage(
        string dbPath,
        object? page = null,
        object? pageSize = null,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        var parsedPageSize = ParseLogPageSize(pageSize);
        if (!File.Exists(dbPath))
        {
            return new RequestLogPageRecord([], 0, 1, parsedPageSize);
        }

        var parsedPage = ParseLogPage(page);
        var offset = (parsedPage - 1) * parsedPageSize;
        var whereClause = BuildLogWhereClause(filters ?? new Dictionary<string, object?>());
        using var connection = OpenConnection(dbPath);

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM request_logs {whereClause.Clause}";
        AddParameters(countCommand, whereClause.Parameters);
        var total = Convert.ToInt32(countCommand.ExecuteScalar() ?? 0);

        using var pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {LogMetadataSelectColumns()}
            FROM request_logs
            {whereClause.Clause}
            ORDER BY id DESC
            LIMIT $limit OFFSET $offset
            """;
        AddParameters(pageCommand, whereClause.Parameters);
        pageCommand.Parameters.AddWithValue("$limit", parsedPageSize);
        pageCommand.Parameters.AddWithValue("$offset", offset);

        using var reader = pageCommand.ExecuteReader();
        var events = new List<RequestLogEventRecord>();
        while (reader.Read())
        {
            events.Add(ReadRequestLogEvent(reader));
        }

        return new RequestLogPageRecord(events, total, parsedPage, parsedPageSize);
    }

    public static RequestLogRecord? ReadLogById(
        string dbPath,
        object? logId,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (!File.Exists(dbPath))
        {
            return null;
        }

        if (!TryConvertInt64(logId, out var parsedId))
        {
            return null;
        }

        var whereClause = BuildLogWhereClause(filters ?? new Dictionary<string, object?>(), tableAlias: "l");
        var idCondition = "l.id = $id";
        var finalWhereClause = whereClause.Clause.Length == 0
            ? $"WHERE {idCondition}"
            : $"{whereClause.Clause} AND {idCondition}";
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {LogMetadataSelectColumns("l")},
                {LogDetailSelectColumns("d")}
            FROM request_logs l
            LEFT JOIN request_log_details d ON d.log_id = l.id
            {finalWhereClause}
            """;
        AddParameters(command, whereClause.Parameters);
        command.Parameters.AddWithValue("$id", parsedId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRequestLog(reader) : null;
    }

    public static IReadOnlyDictionary<string, object> ReadLogFilterOptions(
        string dbPath,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (!File.Exists(dbPath))
        {
            return EmptyLogFilterOptions();
        }

        var whereClause = BuildLogWhereClause(filters ?? new Dictionary<string, object?>());
        using var connection = OpenConnection(dbPath);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["request_ids"] = DistinctTextValues(connection, "request_id", whereClause),
            ["models"] = DistinctTextValues(connection, "model", whereClause),
            ["upstream_models"] = DistinctTextValues(connection, "upstream_model", whereClause),
            ["channel_ids"] = DistinctTextValues(connection, "channel_id", whereClause),
            ["owner_usernames"] = DistinctTextValues(connection, "owner_username", whereClause),
            ["paths"] = DistinctTextValues(connection, "path", whereClause),
            ["status_codes"] = DistinctIntValues(connection, "status_code", whereClause),
            ["api_key_ids"] = DistinctIntValues(connection, "api_key_id", whereClause),
            ["request_statuses"] = new List<string> { "success", "failed" }
        };
    }

    public static IReadOnlyDictionary<string, object> ReadLogFilterOption(
        string dbPath,
        string field,
        object? query = null,
        IReadOnlyDictionary<string, object?>? filters = null)
    {
        if (!File.Exists(dbPath))
        {
            return EmptyLogFilterOptions();
        }

        if (field == "request_status")
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["request_statuses"] = new List<string> { "success", "failed" }
            };
        }

        if (!LogFilterFields.TryGetValue(field, out var option))
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        var whereClause = BuildLogWhereClause(filters ?? new Dictionary<string, object?>());
        using var connection = OpenConnection(dbPath);
        var values = option.OptionType == "int"
            ? (object)DistinctIntValues(connection, field, whereClause, query)
            : DistinctTextValues(connection, field, whereClause, query);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [option.OptionKey] = values
        };
    }

    public static StatsRecord ReadStats(
        string dbPath,
        string? rangeKey = "1h",
        object? startTs = null,
        object? endTs = null,
        string? ownerUsername = null)
    {
        var resolved = ResolveStatsRange(rangeKey, startTs, endTs);
        if (!File.Exists(dbPath))
        {
            return EmptyStatsResponse(resolved);
        }

        using var connection = OpenConnection(dbPath);
        var ownerClause = string.IsNullOrWhiteSpace(ownerUsername) ? string.Empty : "AND owner_username = $owner_username";
        var bucketSeconds = resolved.GranularityMinutes * 60.0;
        var bucketCount = Math.Max(
            1,
            (int)Math.Floor((resolved.EndTs - resolved.StartTs + bucketSeconds - 1) / bucketSeconds));
        var points = new List<StatsPointRecord>();

        for (var index = 0; index < bucketCount; index++)
        {
            var bucketStart = resolved.StartTs + index * bucketSeconds;
            var bucketEnd = resolved.StartTs + (index + 1) * bucketSeconds;
            using var bucketCommand = connection.CreateCommand();
            bucketCommand.CommandText = $"""
                SELECT
                    SUM(cost) AS total_cost,
                    SUM(input_tokens) AS total_input,
                    SUM(cached_tokens) AS total_cached,
                    SUM(output_tokens) AS total_output,
                    AVG(CASE WHEN ttft_ms > 0 THEN ttft_ms END) AS avg_ttft,
                    COUNT(*) AS request_count
                FROM request_logs
                WHERE created_at >= $bucket_start
                  AND created_at < $bucket_end
                  {ownerClause}
                """;
            bucketCommand.Parameters.AddWithValue("$bucket_start", bucketStart);
            bucketCommand.Parameters.AddWithValue("$bucket_end", bucketEnd);
            AddOwnerParameter(bucketCommand, ownerUsername);
            using var reader = bucketCommand.ExecuteReader();
            reader.Read();
            var cost = ReadDouble(reader, "total_cost");
            var inputTokens = ReadInt(reader, "total_input");
            var cachedTokens = ReadInt(reader, "total_cached");
            var outputTokens = ReadInt(reader, "total_output");
            var requestCount = ReadInt(reader, "request_count");
            var avgTtft = ReadNullableDouble(reader, "avg_ttft");
            var cacheDenominator = inputTokens + cachedTokens;
            points.Add(new StatsPointRecord(
                TimestampToIso(bucketEnd),
                Math.Round(cost, 6),
                inputTokens,
                cachedTokens,
                outputTokens,
                avgTtft is null ? null : Math.Round(avgTtft.Value, 1),
                cacheDenominator > 0 ? Math.Round((double)cachedTokens / cacheDenominator, 4) : null,
                requestCount > 0 ? Math.Round((double)requestCount / resolved.GranularityMinutes, 2) : 0));
        }

        var modelDistribution = ReadModelDistribution(
            connection,
            resolved.StartTs,
            resolved.EndTs,
            ownerClause,
            ownerUsername);
        var summary = ReadStatsSummary(
            connection,
            resolved.StartTs,
            resolved.EndTs,
            resolved.GranularityMinutes,
            ownerClause,
            ownerUsername,
            points);

        return new StatsRecord(
            resolved.RangeKey,
            TimestampToIso(resolved.StartTs),
            TimestampToIso(resolved.EndTs),
            resolved.GranularityMinutes,
            PricingDefaults.UsdCnyRate,
            summary,
            points,
            modelDistribution);
    }
}
