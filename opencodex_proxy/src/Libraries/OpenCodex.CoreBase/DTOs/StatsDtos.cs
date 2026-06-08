namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示按时间桶聚合的统计点。
/// </summary>
/// <param name="time">时间桶标签。</param>
/// <param name="cost">该时间桶内累计的成本。</param>
/// <param name="inputTokens">该时间桶内累计的输入 token 数。</param>
/// <param name="cachedTokens">该时间桶内累计的缓存输入 token 数。</param>
/// <param name="outputTokens">该时间桶内累计的输出 token 数。</param>
/// <param name="avgTtftMs">平均首 token 时间，单位为毫秒（如果可用）。</param>
/// <param name="cacheHitRate">缓存命中率（如果可用）。</param>
/// <param name="rpm">该时间桶的每分钟请求数。</param>
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
    /// <summary>
    /// 获取时间桶标签。
    /// </summary>
    public string Time { get; } = time;

    /// <summary>
    /// 获取该时间桶内累计的成本。
    /// </summary>
    public double Cost { get; } = cost;

    /// <summary>
    /// 获取该时间桶内累计的输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取该时间桶内累计的缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取该时间桶内累计的输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;

    /// <summary>
    /// 获取平均首 token 时间，单位为毫秒（如果可用）。
    /// </summary>
    public double? AvgTtftMs { get; } = avgTtftMs;

    /// <summary>
    /// 获取缓存命中率（如果可用）。
    /// </summary>
    public double? CacheHitRate { get; } = cacheHitRate;

    /// <summary>
    /// 获取该时间桶的每分钟请求数。
    /// </summary>
    public double Rpm { get; } = rpm;
}

/// <summary>
/// 表示选定范围内的聚合统计。
/// </summary>
/// <param name="requestCount">总请求数。</param>
/// <param name="successCount">成功请求数。</param>
/// <param name="recent1hRequestCount">最近一小时请求数。</param>
/// <param name="inputTokens">总输入 token 数。</param>
/// <param name="cachedTokens">总缓存输入 token 数。</param>
/// <param name="outputTokens">总输出 token 数。</param>
/// <param name="totalTokens">总 token 数。</param>
/// <param name="recent1hTokens">最近一小时 token 数。</param>
/// <param name="cost">总成本。</param>
/// <param name="recent1hCost">最近一小时成本。</param>
/// <param name="rpm">选定范围内的每分钟请求数。</param>
/// <param name="tpm">选定范围内的每分钟 token 数。</param>
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
    /// <summary>
    /// 获取总请求数。
    /// </summary>
    public int RequestCount { get; } = requestCount;

    /// <summary>
    /// 获取成功请求数。
    /// </summary>
    public int SuccessCount { get; } = successCount;

    /// <summary>
    /// 获取最近一小时请求数。
    /// </summary>
    public int Recent1hRequestCount { get; } = recent1hRequestCount;

    /// <summary>
    /// 获取总输入 token 数。
    /// </summary>
    public int InputTokens { get; } = inputTokens;

    /// <summary>
    /// 获取总缓存输入 token 数。
    /// </summary>
    public int CachedTokens { get; } = cachedTokens;

    /// <summary>
    /// 获取总输出 token 数。
    /// </summary>
    public int OutputTokens { get; } = outputTokens;

    /// <summary>
    /// 获取总 token 数。
    /// </summary>
    public int TotalTokens { get; } = totalTokens;

    /// <summary>
    /// 获取最近一小时 token 数。
    /// </summary>
    public int Recent1hTokens { get; } = recent1hTokens;

    /// <summary>
    /// 获取总成本。
    /// </summary>
    public double Cost { get; } = cost;

    /// <summary>
    /// 获取最近一小时成本。
    /// </summary>
    public double Recent1hCost { get; } = recent1hCost;

    /// <summary>
    /// 获取选定范围内的每分钟请求数。
    /// </summary>
    public double Rpm { get; } = rpm;

    /// <summary>
    /// 获取选定范围内的每分钟 token 数。
    /// </summary>
    public double Tpm { get; } = tpm;
}

/// <summary>
/// 表示按模型分组的请求数。
/// </summary>
/// <param name="model">模型名称。</param>
/// <param name="count">该模型的请求数。</param>
public sealed class ModelDistributionDto(
    string model,
    int count)
{
    /// <summary>
    /// 获取模型名称。
    /// </summary>
    public string Model { get; } = model;

    /// <summary>
    /// 获取该模型的请求数。
    /// </summary>
    public int Count { get; } = count;
}

/// <summary>
/// 表示选定范围内的请求、token 和成本统计。
/// </summary>
/// <param name="range">选定范围标签。</param>
/// <param name="start">范围开始时间戳。</param>
/// <param name="end">范围结束时间戳。</param>
/// <param name="granularityMinutes">统计点粒度，单位为分钟。</param>
/// <param name="currencyRate">用于成本展示的货币换算率。</param>
/// <param name="summary">聚合统计摘要。</param>
/// <param name="points">按时间桶聚合的统计点。</param>
/// <param name="modelDistribution">选定范围内的模型分布。</param>
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
    /// <summary>
    /// 获取选定范围标签。
    /// </summary>
    public string Range { get; } = range;

    /// <summary>
    /// 获取范围开始时间戳。
    /// </summary>
    public string Start { get; } = start;

    /// <summary>
    /// 获取范围结束时间戳。
    /// </summary>
    public string End { get; } = end;

    /// <summary>
    /// 获取统计点粒度，单位为分钟。
    /// </summary>
    public int GranularityMinutes { get; } = granularityMinutes;

    /// <summary>
    /// 获取用于成本展示的货币换算率。
    /// </summary>
    public double CurrencyRate { get; } = currencyRate;

    /// <summary>
    /// 获取聚合统计摘要。
    /// </summary>
    public StatsSummaryDto Summary { get; } = summary;

    /// <summary>
    /// 获取按时间桶聚合的统计点。
    /// </summary>
    public IReadOnlyList<StatsPointDto> Points { get; } = points;

    /// <summary>
    /// 获取选定范围内的模型分布。
    /// </summary>
    public IReadOnlyList<ModelDistributionDto> ModelDistribution { get; } = modelDistribution;
}
