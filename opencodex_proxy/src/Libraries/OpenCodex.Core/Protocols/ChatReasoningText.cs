using System.Text;
using System.Text.Json;

namespace OpenCodex.Core.Protocols;

/// <summary>
/// Chat 协议侧思维链纯文本的统一提取入口。上游存在两种口径：
/// <list type="bullet">
/// <item>DeepSeek 官方风格：<c>reasoning_content</c> 直接给纯文本。</item>
/// <item>OpenRouter 风格（含 api.commandcode.ai）：<c>reasoning</c> 给纯文本，并同时给出
/// <c>reasoning_details</c> 数组，元素形如
/// <c>{"type":"reasoning.text","text":"...","format":"unknown","index":0}</c>。</item>
/// </list>
/// 同一条增量里这几个字段表达的是同一段思考，所以按
/// <c>reasoning_content</c> → <c>reasoning</c> → <c>reasoning_details</c> 的优先级只取一份，
/// 避免拼接出重复文本。<c>reasoning.encrypted</c> 这类没有明文的条目不含 <c>text</c>/<c>summary</c>，
/// 自然被跳过。
/// </summary>
internal static class ChatReasoningText
{
    private static readonly string[] PlainTextKeys = ["reasoning_content", "reasoning"];

    /// <summary>
    /// 从 Chat 的 <c>choices[].delta</c> 或 <c>choices[].message</c> 中取出思维链文本，
    /// 没有则返回空字符串。
    /// </summary>
    public static string Extract(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return string.Empty;
        }

        foreach (var key in PlainTextKeys)
        {
            var text = AsText(Lookup(source, key));
            if (text.Length > 0)
            {
                return text;
            }
        }

        return AsText(Lookup(source, "reasoning_details"));
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return null;
        }

        return value is JsonElement element ? StreamCaptureValues.FromJsonElement(element) : value;
    }

    private static string AsText(object? value)
    {
        return value switch
        {
            string text => text,
            IEnumerable<object?> items => Flatten(items),
            _ => string.Empty
        };
    }

    private static string Flatten(IEnumerable<object?> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            var normalized = item is JsonElement element
                ? StreamCaptureValues.FromJsonElement(element)
                : item;
            if (normalized is not IReadOnlyDictionary<string, object?> detail)
            {
                continue;
            }

            var text = Lookup(detail, "text") as string;
            if (string.IsNullOrEmpty(text))
            {
                text = Lookup(detail, "summary") as string;
            }

            if (!string.IsNullOrEmpty(text))
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }
}
