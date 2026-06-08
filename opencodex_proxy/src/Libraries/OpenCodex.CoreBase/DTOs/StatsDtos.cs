namespace OpenCodex.CoreBase.DTOs;

public sealed class StatsPointDto(
    string time,
    double cost,
    int inputTokens,
    int cachedTokens,
    int outputTokens,
    double? avgTtftMs,
    double? cacheHitRate,
    double rpm)
{
    public string Time { get; } = time;

    public double Cost { get; } = cost;

    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;

    public double? AvgTtftMs { get; } = avgTtftMs;

    public double? CacheHitRate { get; } = cacheHitRate;

    public double Rpm { get; } = rpm;
}

public sealed class StatsSummaryDto(
    int requestCount,
    int successCount,
    int recent1hRequestCount,
    int inputTokens,
    int cachedTokens,
    int outputTokens,
    int totalTokens,
    int recent1hTokens,
    double cost,
    double recent1hCost,
    double rpm,
    double tpm)
{
    public int RequestCount { get; } = requestCount;

    public int SuccessCount { get; } = successCount;

    public int Recent1hRequestCount { get; } = recent1hRequestCount;

    public int InputTokens { get; } = inputTokens;

    public int CachedTokens { get; } = cachedTokens;

    public int OutputTokens { get; } = outputTokens;

    public int TotalTokens { get; } = totalTokens;

    public int Recent1hTokens { get; } = recent1hTokens;

    public double Cost { get; } = cost;

    public double Recent1hCost { get; } = recent1hCost;

    public double Rpm { get; } = rpm;

    public double Tpm { get; } = tpm;
}

public sealed class ModelDistributionDto(
    string model,
    int count)
{
    public string Model { get; } = model;

    public int Count { get; } = count;
}

public sealed class StatsDto(
    string range,
    string start,
    string end,
    int granularityMinutes,
    double currencyRate,
    StatsSummaryDto summary,
    IReadOnlyList<StatsPointDto> points,
    IReadOnlyList<ModelDistributionDto> modelDistribution)
{
    public string Range { get; } = range;

    public string Start { get; } = start;

    public string End { get; } = end;

    public int GranularityMinutes { get; } = granularityMinutes;

    public double CurrencyRate { get; } = currencyRate;

    public StatsSummaryDto Summary { get; } = summary;

    public IReadOnlyList<StatsPointDto> Points { get; } = points;

    public IReadOnlyList<ModelDistributionDto> ModelDistribution { get; } = modelDistribution;
}
