using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 表示一次流式写出过程中采集到的关键时序指标。
/// </summary>
public sealed class StreamWriteMetrics
{
    public StreamWriteMetrics(
        int? ttftMs = null,
        int? firstSseEventMs = null,
        int? firstReasoningSummaryTextDeltaMs = null,
        int? firstOutputTextDeltaMs = null,
        int? firstFunctionCallArgumentsDeltaMs = null,
        int? completedEventMs = null)
    {
        TtftMs = ttftMs;
        FirstSseEventMs = firstSseEventMs;
        FirstReasoningSummaryTextDeltaMs = firstReasoningSummaryTextDeltaMs;
        FirstOutputTextDeltaMs = firstOutputTextDeltaMs;
        FirstFunctionCallArgumentsDeltaMs = firstFunctionCallArgumentsDeltaMs;
        CompletedEventMs = completedEventMs;
    }

    [JsonPropertyName("ttft_ms")]
    public int? TtftMs { get; set; }

    [JsonPropertyName("first_sse_event_ms")]
    public int? FirstSseEventMs { get; set; }

    [JsonPropertyName("first_reasoning_summary_text_delta_ms")]
    public int? FirstReasoningSummaryTextDeltaMs { get; set; }

    [JsonPropertyName("first_output_text_delta_ms")]
    public int? FirstOutputTextDeltaMs { get; set; }

    [JsonPropertyName("first_function_call_arguments_delta_ms")]
    public int? FirstFunctionCallArgumentsDeltaMs { get; set; }

    [JsonPropertyName("completed_event_ms")]
    public int? CompletedEventMs { get; set; }

    [JsonIgnore]
    public bool HasValues =>
        TtftMs.HasValue
        || FirstSseEventMs.HasValue
        || FirstReasoningSummaryTextDeltaMs.HasValue
        || FirstOutputTextDeltaMs.HasValue
        || FirstFunctionCallArgumentsDeltaMs.HasValue
        || CompletedEventMs.HasValue;
}
