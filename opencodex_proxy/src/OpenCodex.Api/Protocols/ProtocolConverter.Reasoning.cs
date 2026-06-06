namespace OpenCodex.Api.Protocols;

public static partial class ProtocolConverter
{
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
        if (!string.IsNullOrEmpty(encryptedContent))
        {
            return encryptedContent;
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
            ("encrypted_content", text),
            ("status", "completed"));
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

            var annotation = Obj(
                ("type", GetValue(source, "type") ?? "url_citation"),
                ("url", GetValue(source, "url") ?? string.Empty),
                ("title", GetValue(source, "title") ?? string.Empty));
            var snippet = GetValue(source, "snippet") ?? GetValue(source, "summary");
            if (snippet is not null)
            {
                annotation["snippet"] = snippet;
            }

            result.Add(annotation);
        }

        return result;
    }
}
