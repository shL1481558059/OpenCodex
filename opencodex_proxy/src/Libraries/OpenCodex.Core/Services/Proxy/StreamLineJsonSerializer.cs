using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.Services.Proxy;

/// <summary>
/// 把流式捕获的 SSE 行序列化为日志正文用的 JSON 字节。
/// </summary>
/// <remarks>
/// 直接产出 UTF-8 字节,不经过 <c>Dictionary</c> 列表与中间 JSON 字符串。
/// 一次 42 MB 的流日志原先要额外产生约 18 万个字典对象和一份 UTF-16 字符串,
/// 这里是内存峰值的主要来源。
/// 输出必须与原先 <c>JsonSerializer.Serialize</c> 的结果逐字节一致,
/// 否则内容哈希变化会导致历史块无法复用。
/// </remarks>
internal static class StreamLineJsonSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] Serialize(IReadOnlyList<ProxyRequestStreamLineCapture> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var buffer = new ArrayBufferWriter<byte>(EstimateCapacity(lines));
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var line in lines.OrderBy(item => item.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", line.Sequence);
                writer.WriteString("source", line.Source);
                writer.WriteString("raw_line", line.RawLine);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static int EstimateCapacity(IReadOnlyList<ProxyRequestStreamLineCapture> lines)
    {
        // 每个元素约 "{"sequence":N,"source":"S","raw_line":"R"}," 的开销。
        var estimate = 2L;
        foreach (var line in lines)
        {
            estimate += line.RawLine.Length + line.Source.Length + 48L;
        }

        return estimate >= int.MaxValue ? int.MaxValue : (int)estimate;
    }
}
