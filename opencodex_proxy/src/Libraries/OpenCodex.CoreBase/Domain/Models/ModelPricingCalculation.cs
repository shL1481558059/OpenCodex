using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCodex.Core.Domain;

namespace OpenCodex.CoreBase.Domain.Models;

/// 谷段时间窗口。同一份形状用于 API 请求、响应、导入导出、数据库 JSON 列和费用快照，
/// 避免多份副本互相漂移。规范化后的窗口不跨午夜，Days 一定是显式的 ISO 星期列表。
public sealed class PricingOffPeakWindow
{
    /// 本地起始时间，格式 HH:mm，闭端。
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    /// 本地结束时间，格式 HH:mm，开端；允许 24:00 表示当日结束。
    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    /// 生效星期，ISO-8601 编号，1 表示周一、7 表示周日。
    [JsonPropertyName("days")]
    public List<int> Days { get; set; } = [];
}

public sealed class ModelUsageVector
{
    public ModelUsageVector(
        int inputTokens,
        int outputTokens,
        int cacheWriteTokens,
        int cacheReadTokens,
        int requestCount = 1)
    {
        InputTokens = Math.Max(0, inputTokens);
        OutputTokens = Math.Max(0, outputTokens);
        CacheWriteTokens = Math.Max(0, cacheWriteTokens);
        CacheReadTokens = Math.Max(0, cacheReadTokens);
        RequestCount = Math.Max(0, requestCount);
    }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public int CacheWriteTokens { get; }

    public int CacheReadTokens { get; }

    public int RequestCount { get; }
}

public sealed class ModelPricingCalculationResult
{
    public ModelPricingCalculationResult(
        decimal cost,
        string currency,
        Guid? modelInfoId,
        Guid? channelModelInfoId,
        Guid? pricingPlanId,
        string? providerCode,
        string? modelKey,
        string? matchType,
        string? matchPattern,
        string resolution,
        string pricingPhase,
        string phaseSource,
        string snapshotJson)
    {
        Cost = cost;
        Currency = currency;
        ModelInfoId = modelInfoId;
        ChannelModelInfoId = channelModelInfoId;
        PricingPlanId = pricingPlanId;
        ProviderCode = providerCode;
        ModelKey = modelKey;
        MatchType = matchType;
        MatchPattern = matchPattern;
        Resolution = resolution;
        PricingPhase = pricingPhase;
        PhaseSource = phaseSource;
        SnapshotJson = snapshotJson;
    }

    public decimal Cost { get; }

    public string Currency { get; }

    public Guid? ModelInfoId { get; }

    public Guid? ChannelModelInfoId { get; }

    public Guid? PricingPlanId { get; }

    public string? ProviderCode { get; }

    public string? ModelKey { get; }

    public string? MatchType { get; }

    public string? MatchPattern { get; }

    public string Resolution { get; }

    /// 本次计费命中的时段：peak 或 off_peak。
    public string PricingPhase { get; }

    /// 时段判定来源：disabled / window_hit / window_miss / time_zone_unresolved。
    public string PhaseSource { get; }

    public string SnapshotJson { get; }
}

public sealed class ModelPricingSnapshot
{
    public ModelPricingSnapshot(
        string resolution,
        string currency,
        decimal cost,
        Guid? modelInfoId,
        Guid? channelModelInfoId,
        Guid? pricingPlanId,
        string? providerCode,
        string? modelKey,
        string? matchType,
        string? matchPattern,
        string pricingPhase,
        string phaseSource,
        double billingInstant,
        string timeZone,
        PricingOffPeakWindow? matchedWindow,
        IReadOnlyList<ModelPricingSnapshotRule> rules)
    {
        Resolution = resolution;
        Currency = currency;
        Cost = cost;
        ModelInfoId = modelInfoId;
        ChannelModelInfoId = channelModelInfoId;
        PricingPlanId = pricingPlanId;
        ProviderCode = providerCode;
        ModelKey = modelKey;
        MatchType = matchType;
        MatchPattern = matchPattern;
        PricingPhase = pricingPhase;
        PhaseSource = phaseSource;
        BillingInstant = billingInstant;
        TimeZone = timeZone;
        MatchedWindow = matchedWindow;
        Rules = rules;
    }

    [JsonPropertyName("resolution")]
    public string Resolution { get; }

    [JsonPropertyName("currency")]
    public string Currency { get; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; }

    [JsonPropertyName("model_info_id")]
    public Guid? ModelInfoId { get; }

    [JsonPropertyName("channel_model_info_id")]
    public Guid? ChannelModelInfoId { get; }

    [JsonPropertyName("pricing_plan_id")]
    public Guid? PricingPlanId { get; }

    [JsonPropertyName("provider_code")]
    public string? ProviderCode { get; }

    [JsonPropertyName("model_key")]
    public string? ModelKey { get; }

    [JsonPropertyName("match_type")]
    public string? MatchType { get; }

    [JsonPropertyName("match_pattern")]
    public string? MatchPattern { get; }

    [JsonPropertyName("pricing_phase")]
    public string PricingPhase { get; }

    [JsonPropertyName("phase_source")]
    public string PhaseSource { get; }

    /// 计费时刻（Unix 秒），即请求进入网关的时刻。
    [JsonPropertyName("billing_instant")]
    public double BillingInstant { get; }

    [JsonPropertyName("time_zone")]
    public string TimeZone { get; }

    [JsonPropertyName("matched_window")]
    public PricingOffPeakWindow? MatchedWindow { get; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<ModelPricingSnapshotRule> Rules { get; }
}

public sealed class ModelPricingSnapshotRule
{
    public ModelPricingSnapshotRule(
        string billingItem,
        string billingMode,
        int quantity,
        decimal unitPrice,
        decimal cost,
        string appliedPhase)
    {
        BillingItem = billingItem;
        BillingMode = billingMode;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Cost = cost;
        AppliedPhase = appliedPhase;
    }

    [JsonPropertyName("billing_item")]
    public string BillingItem { get; }

    [JsonPropertyName("billing_mode")]
    public string BillingMode { get; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; }

    /// 这条规则实际采用的参数集：peak 或 off_peak。
    /// 未开启峰谷的计费项即使在谷段也是 peak。
    [JsonPropertyName("applied_phase")]
    public string AppliedPhase { get; }
}

/// 一次请求的时段判定结果。
public sealed class PricingPhaseEvaluation
{
    public PricingPhaseEvaluation(
        string phase,
        string source,
        string timeZoneId,
        PricingOffPeakWindow? matchedWindow)
    {
        Phase = phase;
        Source = source;
        TimeZoneId = timeZoneId;
        MatchedWindow = matchedWindow;
    }

    public string Phase { get; }

    public string Source { get; }

    public string TimeZoneId { get; }

    public PricingOffPeakWindow? MatchedWindow { get; }

    public bool IsOffPeak => Phase == PricingPhases.OffPeak;
}

/// 谷段窗口的规范化、序列化与时段判定。纯函数，不依赖数据库与配置。
/// 判定只接受规范化形状（不跨午夜），写入路径负责规范化。
public static class PricingWindowCalendar
{
    /// 单个价格计划允许配置的窗口条数上限（按用户输入的条数计，拆分后可能翻倍）。
    public const int MaxWindows = 24;

    private const int MinutesPerDay = 1440;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> TimeZoneCache = new(StringComparer.Ordinal);

    /// 校验 IANA 时区 ID。空值表示未启用峰谷；非法值抛出 ArgumentException。
    public static string NormalizeTimeZoneId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (ResolveTimeZone(text) is null)
        {
            throw new ArgumentException($"time_zone '{text}' is not a known time zone id", nameof(value));
        }

        return text;
    }

    /// 规范化窗口集合：校验格式、展开星期、拆分跨午夜窗口、去重并排序。非法输入抛出 ArgumentException。
    public static IReadOnlyList<PricingOffPeakWindow> Normalize(IEnumerable<PricingOffPeakWindow>? windows)
    {
        var input = (windows ?? []).ToList();
        if (input.Count == 0)
        {
            return [];
        }

        if (input.Count > MaxWindows)
        {
            throw new ArgumentException(
                $"off_peak_windows supports at most {MaxWindows} entries",
                nameof(windows));
        }

        var spans = new HashSet<(int Start, int End, int Day)>();
        foreach (var window in input)
        {
            var start = ParseMinute(window.Start, "start", allowEndOfDay: false);
            var end = ParseMinute(window.End, "end", allowEndOfDay: true);
            if (start == end)
            {
                throw new ArgumentException("off_peak window start and end must differ", nameof(windows));
            }

            foreach (var day in NormalizeDays(window.Days))
            {
                if (start < end)
                {
                    spans.Add((start, end, day));
                    continue;
                }

                // 跨午夜按「窗口起始日」拆分：当日剩余段 + 次日开头段。
                spans.Add((start, MinutesPerDay, day));
                spans.Add((0, end, NextDay(day)));
            }
        }

        return spans
            .GroupBy(span => (span.Start, span.End))
            .OrderBy(group => group.Key.Start)
            .ThenBy(group => group.Key.End)
            .Select(group => new PricingOffPeakWindow
            {
                Start = FormatMinute(group.Key.Start),
                End = FormatMinute(group.Key.End),
                Days = group.Select(span => span.Day).Distinct().OrderBy(day => day).ToList()
            })
            .ToList();
    }

    /// 规范化并序列化为数据库列值。
    public static string Serialize(IEnumerable<PricingOffPeakWindow>? windows)
    {
        return JsonSerializer.Serialize(Normalize(windows));
    }

    /// 读取数据库列值。非法 JSON 视为未配置，不抛异常。
    public static IReadOnlyList<PricingOffPeakWindow> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PricingOffPeakWindow>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// 判定计费时刻落在峰段还是谷段。
    public static PricingPhaseEvaluation Evaluate(
        string? timeZoneId,
        string? windowsJson,
        DateTimeOffset billingInstant)
    {
        var zoneId = (timeZoneId ?? string.Empty).Trim();
        var windows = Deserialize(windowsJson);
        if (zoneId.Length == 0 || windows.Count == 0)
        {
            return new PricingPhaseEvaluation(
                PricingPhases.Peak,
                PricingPhaseSources.Disabled,
                zoneId,
                null);
        }

        var zone = ResolveTimeZone(zoneId);
        if (zone is null)
        {
            return new PricingPhaseEvaluation(
                PricingPhases.Peak,
                PricingPhaseSources.TimeZoneUnresolved,
                zoneId,
                null);
        }

        var local = TimeZoneInfo.ConvertTime(billingInstant, zone);
        var minute = (local.Hour * 60) + local.Minute;
        var day = IsoDay(local.DayOfWeek);
        foreach (var window in windows)
        {
            if (!TryReadSpan(window, out var start, out var end))
            {
                continue;
            }

            if (!window.Days.Contains(day))
            {
                continue;
            }

            if (minute >= start && minute < end)
            {
                return new PricingPhaseEvaluation(
                    PricingPhases.OffPeak,
                    PricingPhaseSources.WindowHit,
                    zoneId,
                    window);
            }
        }

        return new PricingPhaseEvaluation(
            PricingPhases.Peak,
            PricingPhaseSources.WindowMiss,
            zoneId,
            null);
    }

    private static TimeZoneInfo? ResolveTimeZone(string id)
    {
        return TimeZoneCache.GetOrAdd(id, key =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        });
    }

    private static bool TryReadSpan(PricingOffPeakWindow window, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (!TryParseMinute(window.Start, allowEndOfDay: false, out var parsedStart)
            || !TryParseMinute(window.End, allowEndOfDay: true, out var parsedEnd)
            || parsedStart >= parsedEnd)
        {
            return false;
        }

        start = parsedStart;
        end = parsedEnd;
        return true;
    }

    private static IReadOnlyList<int> NormalizeDays(IEnumerable<int>? days)
    {
        var normalized = (days ?? []).Distinct().OrderBy(day => day).ToList();
        if (normalized.Count == 0)
        {
            return [1, 2, 3, 4, 5, 6, 7];
        }

        foreach (var day in normalized)
        {
            if (day < 1 || day > 7)
            {
                throw new ArgumentException(
                    "off_peak window days must be ISO weekday numbers between 1 and 7",
                    nameof(days));
            }
        }

        return normalized;
    }

    private static int NextDay(int day)
    {
        return day == 7 ? 1 : day + 1;
    }

    private static int IsoDay(DayOfWeek value)
    {
        return value == DayOfWeek.Sunday ? 7 : (int)value;
    }

    private static int ParseMinute(string? value, string field, bool allowEndOfDay)
    {
        if (!TryParseMinute(value, allowEndOfDay, out var minute))
        {
            throw new ArgumentException(
                $"off_peak window {field} must be a HH:mm time of day",
                field);
        }

        return minute;
    }

    private static bool TryParseMinute(string? value, bool allowEndOfDay, out int minute)
    {
        minute = 0;
        var text = (value ?? string.Empty).Trim();
        if (text.Length != 5 || text[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        if (minutes > 59)
        {
            return false;
        }

        if (hour == 24)
        {
            if (!allowEndOfDay || minutes != 0)
            {
                return false;
            }

            minute = MinutesPerDay;
            return true;
        }

        if (hour > 23)
        {
            return false;
        }

        minute = (hour * 60) + minutes;
        return true;
    }

    private static string FormatMinute(int minute)
    {
        return minute == MinutesPerDay
            ? "24:00"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{minute / 60:D2}:{minute % 60:D2}");
    }
}
