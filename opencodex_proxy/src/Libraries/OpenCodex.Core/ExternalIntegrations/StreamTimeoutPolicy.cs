using System.Globalization;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

internal sealed class StreamTimeoutPolicy
{
    // CancellationTokenSource 的定时器最大支持 uint.MaxValue - 1 毫秒。
    private const double MaximumSeconds = (uint.MaxValue - 1d) / 1000d;

    public required TimeSpan FirstContent { get; init; }
    public required TimeSpan Idle { get; init; }
    public required TimeSpan Total { get; init; }

    public static StreamTimeoutPolicy Create(
        IReadOnlyDictionary<string, object?> channel,
        int timeoutSeconds,
        int retryCount,
        int retryAfterCapSeconds)
    {
        var singleSeconds = Math.Clamp((double)timeoutSeconds, 1, MaximumSeconds);
        // 无重试也给持续输出保留十倍时间；所有重试及最长退避共享同一个有限预算。
        var totalSeconds = Math.Min(
            singleSeconds * Math.Max(10d, (double)retryCount + 1) + (double)retryAfterCapSeconds * retryCount,
            MaximumSeconds);
        if (JsonDictionaryValue.Get(channel, "compat") is IReadOnlyDictionary<string, object?> compat
            && compat.TryGetValue("stream_total_timeout_seconds", out var configured))
        {
            var parsed = configured switch
            {
                int value => value,
                long value => value,
                double value => value,
                decimal value => (double)value,
                string value when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
                _ => double.NaN
            };
            if (!double.IsFinite(parsed) || parsed < 0.001 || parsed > MaximumSeconds)
            {
                throw new BadRequestException(
                    $"channel.compat.stream_total_timeout_seconds must be between 0.001 and {MaximumSeconds.ToString(CultureInfo.InvariantCulture)} seconds");
            }

            totalSeconds = parsed;
        }

        return new StreamTimeoutPolicy
        {
            FirstContent = TimeSpan.FromSeconds(singleSeconds),
            Idle = TimeSpan.FromSeconds(singleSeconds),
            Total = TimeSpan.FromSeconds(totalSeconds)
        };
    }
}
