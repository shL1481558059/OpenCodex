namespace OpenCodex.CoreBase.Abstractions;

/// <summary>
/// 表示一次流式写出的首令牌耗时。
/// </summary>
public sealed class StreamWriteMetrics
{
    public StreamWriteMetrics(int? ttftMs = null)
    {
        TtftMs = ttftMs;
    }

    public int? TtftMs { get; set; }
}
