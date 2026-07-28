namespace OpenCodex.Core.Protocols;

using System.Text.Json;

public static partial class ProtocolConverter
{
    /// <summary>
    /// Prefix used in <c>encrypted_content</c> to signal that the value contains
    /// base64-encoded Anthropic thinking blocks (with signatures).
    /// </summary>
    private const string AnthropicThinkingPrefix = "ocxp-thinking-v1:";

    /// <summary>
    /// Tags wrapping reasoning summaries that are downgraded to plain text blocks.
    /// Used when the source protocol only provides a summary (no signed thinking
    /// block), so the text cannot be sent as a native Anthropic thinking block.
    /// </summary>
    private const string ReasoningTextOpenTag = "<previous_thinking>";

    private const string ReasoningTextCloseTag = "</previous_thinking>";

    private static void AppendReasoningContent(Dictionary<string, object?> message, object? reasoningContent)
    {
        var text = StringifyContent(reasoningContent);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var existing = StringifyContent(GetValue(message, "reasoning_content") ?? string.Empty);
        message["reasoning_content"] = string.IsNullOrEmpty(existing) ? text : existing + text;
    }

    private static string ResponsesMetadataItemToText(Dictionary<string, object?> item)
    {
        var exported = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in item)
        {
            if (key is "content" or "encrypted_content")
            {
                continue;
            }

            if (key == "summary" && !IsTruthy(value))
            {
                continue;
            }

            exported[key] = DeepCopy(value);
        }

        if (exported.Count == 0 || exported.Keys.All(key => key == "type"))
        {
            return string.Empty;
        }

        return $"Responses {GetValue(item, "type") ?? "item"}: {JsonDumps(exported)}";
    }

    private static string ResponsesReasoningToText(Dictionary<string, object?> item)
    {
        var encryptedContent = StringifyContent(GetValue(item, "encrypted_content") ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(encryptedContent)
            && TryDecodeAnthropicThinkingText(encryptedContent, out var plainText))
        {
            return plainText;
        }

        var summary = StringifyContent(GetValue(item, "summary") ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(summary))
        {
            return summary;
        }

        return StringifyContent(GetValue(item, "content") ?? string.Empty).Trim();
    }

    private static Dictionary<string, object?> ResponsesReasoningItem(string text)
    {
        return Obj(
            ("type", "reasoning"),
            ("summary", new List<object?> { Obj(("type", "summary_text"), ("text", text)) }),
            ("status", "completed"));
    }

    /// <summary>
    /// Encode Anthropic thinking content blocks into a string suitable for
    /// <c>encrypted_content</c>. Format: <c>ocxp-thinking-v1:<base64></c>
    /// </summary>
    internal static string EncodeAnthropicThinkingBlocks(List<object?> blocks)
    {
        var json = JsonSerializer.Serialize(blocks, JsonOptions);
        return $"{AnthropicThinkingPrefix}{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))}";
    }

    /// <summary>
    /// Try to decode <c>encrypted_content</c> back into the original Anthropic
    /// thinking content blocks (with signatures). Returns false when the value
    /// does not carry the <c>ocxp-thinking-v1:</c> prefix.
    /// </summary>
    internal static bool TryDecodeAnthropicThinkingBlocks(
        string encryptedContent,
        out List<object?> blocks)
    {
        blocks = [];
        if (!encryptedContent.StartsWith(AnthropicThinkingPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var base64 = encryptedContent[AnthropicThinkingPrefix.Length..];
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            blocks = (List<object?>)FromJsonElement(doc.RootElement)!;
            return blocks.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeAnthropicThinkingText(string encryptedContent, out string plainText)
    {
        plainText = string.Empty;
        if (!TryDecodeAnthropicThinkingBlocks(encryptedContent, out var blocks))
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (!TryAsObject(block, out var dict)) continue;
            if (GetString(dict, "type") == "thinking")
            {
                var text = StringifyContent(GetValue(dict, "thinking") ?? string.Empty);
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
        }

        plainText = string.Concat(parts);
        return true;
    }

    private static List<object?> NormalizeAnnotations(object? value)
    {
        if (!TryAsList(value, out var items))
        {
            return [];
        }

        var result = new List<object?>();
        foreach (var item in items)
        {
            if (!TryAsObject(item, out var source))
            {
                continue;
            }

            var nested = ObjectValue(source, "url_citation");
            var annotation = Obj(
                ("type", GetValue(source, "type") ?? "url_citation"),
                ("url", GetValue(source, "url") ?? GetValue(nested, "url") ?? string.Empty),
                ("title", GetValue(source, "title") ?? GetValue(nested, "title") ?? string.Empty));
            foreach (var key in new[] { "start_index", "end_index", "file_id", "filename", "container_id", "index" })
            {
                var field = GetValue(source, key) ?? GetValue(nested, key);
                if (field is not null)
                {
                    annotation[key] = DeepCopy(field);
                }
            }
            var snippet = GetValue(source, "snippet") ?? GetValue(source, "summary");
            if (snippet is not null)
            {
                annotation["snippet"] = snippet;
            }

            result.Add(annotation);
        }

        return result;
    }

    private static List<object?> CanonicalAnnotationsToChat(List<object?> annotations)
    {
        var result = new List<object?>();
        foreach (var item in annotations)
        {
            if (!TryAsObject(item, out var annotation))
            {
                continue;
            }

            if (GetString(annotation, "type") != "url_citation")
            {
                continue;
            }

            var citation = Obj(
                ("url", GetValue(annotation, "url") ?? string.Empty),
                ("title", GetValue(annotation, "title") ?? string.Empty),
                ("start_index", GetValue(annotation, "start_index") ?? 0),
                ("end_index", GetValue(annotation, "end_index") ?? 0));
            result.Add(Obj(("type", "url_citation"), ("url_citation", citation)));
        }

        return result;
    }
}
