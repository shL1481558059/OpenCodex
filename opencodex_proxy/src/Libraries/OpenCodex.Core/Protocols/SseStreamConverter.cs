using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;

namespace OpenCodex.Core.Protocols;

public sealed class ConvertedStreamResult
{
    public Dictionary<string, object?>? UpstreamResponse { get; set; }

    /// <summary>
    /// 原始 Responses API 请求中的 text.format 配置（用于 json_schema 结构化输出兼容）。
    /// 当上游 channel 不支持 Responses API 的 text.format 时，
    /// 转换器可据此将纯文本输出包装为符合 schema 的 JSON。
    /// </summary>
    public TextFormatInfo? TextFormat { get; set; }
}

/// <summary>
/// 表示 Responses API 请求中 text.format 的结构化输出约束信息。
/// </summary>
public sealed class TextFormatInfo
{
    public string Type { get; init; } = string.Empty;
    public string? SchemaName { get; init; }
    public Dictionary<string, object?>? Schema { get; init; }
}

public static partial class SseStreamConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 当 text.format.type == "json_schema" 且上游返回纯文本时，
    /// 尝试将纯文本包装为 schema 所需的 JSON 对象。
    /// 如果文本已经是合法 JSON 对象，则原样返回；
    /// 否则根据 schema 的第一个 required 字段构建 JSON。
    /// </summary>
    internal static string WrapTextForJsonSchema(string text, TextFormatInfo textFormat)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Already valid JSON object? Return as-is
        var trimmed = text.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return text;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON, fall through to wrapping
            }
        }

        var wrapperKey = ExtractFirstSchemaField(textFormat);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [wrapperKey] = text
        }, JsonOptions);
    }

    internal static string ExtractFirstSchemaField(TextFormatInfo textFormat)
    {
        var wrapperKey = "title";
        if (textFormat.Schema is not null
            && textFormat.Schema.TryGetValue("properties", out var propertiesObj)
            && propertiesObj is Dictionary<string, object?> properties)
        {
            if (textFormat.Schema.TryGetValue("required", out var requiredObj)
                && requiredObj is List<object?> required
                && required.Count > 0
                && required[0] is string firstName)
            {
                wrapperKey = firstName;
            }
            else if (properties.Count > 0)
            {
                wrapperKey = properties.Keys.First();
            }
        }
        else if (!string.IsNullOrEmpty(textFormat.SchemaName))
        {
            wrapperKey = textFormat.SchemaName;
        }

        return wrapperKey;
    }
}

public sealed class SseEvent
{
    public SseEvent(string eventName, object? data)
    {
        EventName = eventName;
        Data = data;
    }

    public string EventName { get; }

    public object? Data { get; }
}

internal sealed class ToolCallAggregate
{
    public string? Id { get; set; }
    public string Type { get; set; } = "function";
    public string? Name { get; set; }
    public string Arguments { get; set; } = string.Empty;
}

internal sealed class ToolStreamState
{
    public int? OutputIndex { get; set; }
    public string? ItemId { get; set; }
    public bool ItemAdded { get; set; }
    public int StreamedArgumentsLength { get; set; }
    public ResponsesToolCallKind CallKind { get; set; } = ResponsesToolCallKind.Function;
    public ApplyPatchJsonDeltaDecoder? ApplyPatchDecoder { get; set; }
    public StringBuilder? DecodedInputBuilder { get; set; }
}
