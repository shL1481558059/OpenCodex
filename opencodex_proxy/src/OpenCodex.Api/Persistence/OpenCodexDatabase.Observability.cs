using Microsoft.Data.Sqlite;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private static readonly IReadOnlyDictionary<string, int> StatsRangeGranularity =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1h"] = 1,
            ["6h"] = 5,
            ["24h"] = 15,
            ["7d"] = 120,
            ["30d"] = 720
        };

    private static readonly IReadOnlyDictionary<string, int> StatsRangeHours =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1h"] = 1,
            ["6h"] = 6,
            ["24h"] = 24,
            ["7d"] = 24 * 7,
            ["30d"] = 24 * 30
        };

    private sealed record ResolvedStatsRange(
        string RangeKey,
        double StartTs,
        double EndTs,
        int GranularityMinutes);

    private static StatsRecord EmptyStatsResponse(ResolvedStatsRange resolved)
    {
        return new StatsRecord(
            resolved.RangeKey,
            TimestampToIso(resolved.StartTs),
            TimestampToIso(resolved.EndTs),
            resolved.GranularityMinutes,
            PricingDefaults.UsdCnyRate,
            EmptyStatsSummary(),
            [],
            []);
    }

    private static StatsSummaryRecord EmptyStatsSummary()
    {
        return new StatsSummaryRecord(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);
    }

    private static StatsSummaryRecord ReadStatsSummary(
        SqliteConnection connection,
        double startTs,
        double endTs,
        int granularityMinutes,
        string ownerClause,
        string? ownerUsername,
        IReadOnlyList<StatsPointRecord> points)
    {
        using var command = CreateStatsSummaryCommand(
            connection,
            startTs,
            endTs,
            ownerClause,
            ownerUsername);
        using var reader = command.ExecuteReader();
        reader.Read();
        var requestCount = ReadInt(reader, "request_count");
        var successCount = ReadInt(reader, "success_count");
        var inputTokens = ReadInt(reader, "input_tokens");
        var cachedTokens = ReadInt(reader, "cached_tokens");
        var outputTokens = ReadInt(reader, "output_tokens");
        var cost = ReadDouble(reader, "cost");
        reader.Close();

        var recentStartTs = Math.Max(startTs, endTs - 3600);
        using var recentCommand = CreateStatsSummaryCommand(
            connection,
            recentStartTs,
            endTs,
            ownerClause,
            ownerUsername);
        using var recentReader = recentCommand.ExecuteReader();
        recentReader.Read();
        var recentRequestCount = ReadInt(recentReader, "request_count");
        var recentInputTokens = ReadInt(recentReader, "input_tokens");
        var recentCachedTokens = ReadInt(recentReader, "cached_tokens");
        var recentOutputTokens = ReadInt(recentReader, "output_tokens");
        var recentCost = ReadDouble(recentReader, "cost");

        var latestPoint = points.LastOrDefault();
        var latestTokens = latestPoint is null
            ? 0
            : latestPoint.InputTokens + latestPoint.CachedTokens + latestPoint.OutputTokens;

        return new StatsSummaryRecord(
            requestCount,
            successCount,
            recentRequestCount,
            inputTokens,
            cachedTokens,
            outputTokens,
            inputTokens + cachedTokens + outputTokens,
            recentInputTokens + recentCachedTokens + recentOutputTokens,
            Math.Round(cost, 6),
            Math.Round(recentCost, 6),
            latestPoint?.Rpm ?? 0,
            latestTokens > 0 ? Math.Round((double)latestTokens / granularityMinutes, 2) : 0);
    }

    private static SqliteCommand CreateStatsSummaryCommand(
        SqliteConnection connection,
        double startTs,
        double endTs,
        string ownerClause,
        string? ownerUsername)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*) AS request_count,
                SUM(CASE WHEN status_code < 400 AND (error IS NULL OR error = '') THEN 1 ELSE 0 END) AS success_count,
                SUM(input_tokens) AS input_tokens,
                SUM(cached_tokens) AS cached_tokens,
                SUM(output_tokens) AS output_tokens,
                SUM(cost) AS cost
            FROM request_logs
            WHERE created_at >= $start_ts
              AND created_at < $end_ts
              {ownerClause}
            """;
        command.Parameters.AddWithValue("$start_ts", startTs);
        command.Parameters.AddWithValue("$end_ts", endTs);
        AddOwnerParameter(command, ownerUsername);
        return command;
    }

    private static List<ModelDistributionRecord> ReadModelDistribution(
        SqliteConnection connection,
        double startTs,
        double endTs,
        string ownerClause,
        string? ownerUsername)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT model, COUNT(*) AS cnt
            FROM request_logs
            WHERE created_at >= $start_ts
              AND created_at < $end_ts
              {ownerClause}
            GROUP BY model
            ORDER BY cnt DESC
            LIMIT 20
            """;
        command.Parameters.AddWithValue("$start_ts", startTs);
        command.Parameters.AddWithValue("$end_ts", endTs);
        AddOwnerParameter(command, ownerUsername);
        using var reader = command.ExecuteReader();
        var result = new List<ModelDistributionRecord>();
        while (reader.Read())
        {
            var model = reader.IsDBNull(reader.GetOrdinal("model"))
                ? "unknown"
                : reader.GetString(reader.GetOrdinal("model"));
            result.Add(new ModelDistributionRecord(
                model.Length == 0 ? "unknown" : model,
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("cnt")))));
        }

        return result;
    }

    private static ResolvedStatsRange ResolveStatsRange(
        string? rangeKey,
        object? startTs,
        object? endTs)
    {
        var normalizedRange = (rangeKey ?? "1h").Trim();
        var now = UnixTimeSeconds();
        if (normalizedRange == "custom")
        {
            var parsedEnd = ParseTimestamp(endTs);
            var parsedStart = ParseTimestamp(startTs);
            var endValue = parsedEnd ?? now;
            var startValue = parsedStart ?? endValue - 3600;
            if (startValue >= endValue)
            {
                startValue = endValue - 3600;
            }

            return new ResolvedStatsRange(
                "custom",
                startValue,
                endValue,
                StatsGranularityForSeconds(endValue - startValue));
        }

        if (!StatsRangeHours.ContainsKey(normalizedRange))
        {
            normalizedRange = "1h";
        }

        var seconds = StatsRangeHours[normalizedRange] * 3600.0;
        return new ResolvedStatsRange(
            normalizedRange,
            now - seconds,
            now,
            StatsRangeGranularity[normalizedRange]);
    }

    private static int StatsGranularityForSeconds(double seconds)
    {
        var minutes = Math.Max(1, seconds / 60);
        const int targetPoints = 72;
        var rawGranularity = Math.Max(1, (int)Math.Floor((minutes + targetPoints - 1) / targetPoints));
        foreach (var choice in new[] { 1, 3, 5, 10, 15, 30, 60, 120, 360, 720, 1440 })
        {
            if (rawGranularity <= choice)
            {
                return choice;
            }
        }

        return 1440;
    }
}
