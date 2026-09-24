using System.Text;
using OpenCodex.Core.Protocols;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 记录流式响应中最后一行可读的 SSE 数据行，供失败时写入请求日志的错误信息。
/// </summary>
/// <remarks>
/// 只保留一个字符串引用，内存占用与流长度无关；完整 SSE 行不再进入日志。
/// </remarks>
internal sealed class StreamLogCapture
{
    internal const int MaxErrorTextLength = 2000;

    private const string DataPrefix = "data:";
    private const string EventPrefix = "event:";
    private const string DonePayload = "[DONE]";
    private const string TruncationMarker = "…";

    private readonly string _entryProtocol;
    private string? _lastDataLine;

    /// <summary>
    /// 初始化 <see cref="StreamLogCapture"/> 类的新实例。
    /// </summary>
    /// <param name="entryProtocol">客户端协议形态，决定终止事件判定规则。</param>
    public StreamLogCapture(string entryProtocol)
    {
        _entryProtocol = entryProtocol;
    }

    /// <summary>
    /// 获取已捕获的最后一行数据行。
    /// </summary>
    public string? LastDataLine => _lastDataLine;

    /// <summary>
    /// 获取客户端协议是否收到过终止事件。
    /// </summary>
    public bool SawTerminalEvent { get; private set; }

    /// <summary>
    /// 观察单行流内容，命中可读的数据行时替换已保存的最后一行。
    /// </summary>
    /// <param name="rawLine">按换行拆分后的单行原文。</param>
    public void Observe(string rawLine)
    {
        var trimmed = rawLine.TrimStart();
        if (trimmed.StartsWith(EventPrefix, StringComparison.Ordinal))
        {
            if (IsTerminalEventName(trimmed[EventPrefix.Length..].Trim()))
            {
                SawTerminalEvent = true;
            }

            return;
        }

        if (!trimmed.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var payload = trimmed[DataPrefix.Length..].Trim();
        if (string.Equals(payload, DonePayload, StringComparison.Ordinal))
        {
            SawTerminalEvent = true;
            return;
        }

        if (IsTerminalPayload(payload))
        {
            SawTerminalEvent = true;
        }

        if (payload.Length == 0)
        {
            return;
        }

        _lastDataLine = rawLine.Trim();
    }

    private bool IsTerminalEventName(string eventName)
    {
        return _entryProtocol switch
        {
            ProtocolConverter.Responses => eventName is "response.completed" or "response.failed" or "response.incomplete",
            ProtocolConverter.Messages => eventName is "message_stop" or "error",
            _ => false
        };
    }

    private bool IsTerminalPayload(string payload)
    {
        return _entryProtocol switch
        {
            ProtocolConverter.Responses => payload.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal)
                || payload.Contains("\"type\":\"response.failed\"", StringComparison.Ordinal)
                || payload.Contains("\"type\":\"response.incomplete\"", StringComparison.Ordinal),
            ProtocolConverter.Messages => payload.Contains("\"type\":\"message_stop\"", StringComparison.Ordinal)
                || payload.Contains("\"type\":\"error\"", StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>
    /// 合成写入日志错误字段的文本。
    /// </summary>
    /// <param name="error">原始错误消息（如果可用）。</param>
    /// <param name="termination">流终止原因。</param>
    /// <returns>错误消息、终止原因与最后一行数据行组成的文本。</returns>
    public string ComposeErrorText(string? error, StreamCaptureTermination termination)
    {
        var builder = new StringBuilder();
        builder.Append(string.IsNullOrWhiteSpace(error)
            ? DefaultErrorMessage(termination)
            : error.Trim());
        builder.Append("｜终止:").Append(termination);
        if (!string.IsNullOrWhiteSpace(_lastDataLine))
        {
            builder.Append("｜最后SSE:").Append(_lastDataLine);
        }

        return Truncate(builder.ToString());
    }

    private static string DefaultErrorMessage(StreamCaptureTermination termination)
    {
        return termination switch
        {
            StreamCaptureTermination.UnexpectedEnd => "upstream stream ended before its terminal event",
            StreamCaptureTermination.UpstreamError => "upstream stream failed",
            StreamCaptureTermination.ClientCancelled => "client cancelled the stream",
            _ => "proxy stream failed"
        };
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxErrorTextLength
            ? text
            : string.Concat(
                text.AsSpan(0, MaxErrorTextLength - TruncationMarker.Length),
                TruncationMarker);
    }
}
