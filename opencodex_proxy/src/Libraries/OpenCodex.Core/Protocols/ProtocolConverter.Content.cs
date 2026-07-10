namespace OpenCodex.Core.Protocols;

public static partial class ProtocolConverter
{
    private static object? ResponsesContentToChatContent(object? content)
    {
        if (content is string text)
        {
            return text;
        }

        if (TryAsList(content, out var blocks))
        {
            var result = new List<object?>();
            foreach (var item in blocks)
            {
                if (!TryAsObject(item, out var block))
                {
                    continue;
                }

                if (GetString(block, "type") is "input_text" or "output_text" or "text")
                {
                    result.Add(Obj(("type", "text"), ("text", GetValue(block, "text") ?? string.Empty)));
                }
                else if (GetString(block, "type") == "input_image")
                {
                    var imageUrl = Obj(("url", GetValue(block, "image_url") ?? string.Empty));
                    if (GetValue(block, "detail") is not null)
                    {
                        imageUrl["detail"] = GetValue(block, "detail");
                    }

                    result.Add(Obj(("type", "image_url"), ("image_url", imageUrl)));
                }
                else if (GetString(block, "type") == "input_file")
                {
                    var file = new Dictionary<string, object?>(StringComparer.Ordinal);
                    CopyIfPresent(block, file, "file_id");
                    CopyIfPresent(block, file, "file_data");
                    CopyIfPresent(block, file, "filename");
                    CopyIfPresent(block, file, "file_url");
                    result.Add(Obj(("type", "file"), ("file", file)));
                }
                else
                {
                    result.Add(DeepCopy(block));
                }
            }

            if (result.Count == 1 && TryAsObject(result[0], out var single) && GetString(single, "type") == "text")
            {
                return GetValue(single, "text") ?? string.Empty;
            }

            return result;
        }

        return StringifyContent(content);
    }

    private static List<object?> ChatContentToResponsesContent(object? content, string role)
    {
        var textType = role == "assistant" ? "output_text" : "input_text";
        if (content is string text)
        {
            return [Obj(("type", textType), ("text", text))];
        }

        if (TryAsList(content, out var blocks))
        {
            var result = new List<object?>();
            foreach (var item in blocks)
            {
                if (TryAsObject(item, out var block) && GetString(block, "type") is "text" or "input_text" or "output_text")
                {
                    result.Add(Obj(("type", textType), ("text", GetValue(block, "text") ?? string.Empty)));
                }
                else if (TryAsObject(item, out block) && GetString(block, "type") == "image_url")
                {
                    var image = ObjectValue(block, "image_url");
                    var converted = Obj(
                        ("type", "input_image"),
                        ("image_url", GetValue(image, "url") ?? string.Empty));
                    CopyIfPresent(image, converted, "detail");
                    result.Add(converted);
                }
                else if (TryAsObject(item, out block) && GetString(block, "type") == "file")
                {
                    var file = ObjectValue(block, "file");
                    var converted = Obj(("type", "input_file"));
                    CopyIfPresent(file, converted, "file_id");
                    CopyIfPresent(file, converted, "file_data");
                    CopyIfPresent(file, converted, "filename");
                    CopyIfPresent(file, converted, "file_url");
                    result.Add(converted);
                }
                else
                {
                    result.Add(DeepCopy(item));
                }
            }

            return result;
        }

        return [Obj(("type", textType), ("text", StringifyContent(content)))];
    }

    private static object? AnthropicContentToChatContent(object? content)
    {
        if (content is string text)
        {
            return text;
        }

        if (TryAsList(content, out var blocks))
        {
            var result = new List<object?>();
            foreach (var item in blocks)
            {
                if (!TryAsObject(item, out var block))
                {
                    continue;
                }

                if (GetString(block, "type") == "text")
                {
                    result.Add(Obj(("type", "text"), ("text", GetValue(block, "text") ?? string.Empty)));
                }
                else if (GetString(block, "type") == "tool_result")
                {
                    result.Add(Obj(("type", "text"), ("text", StringifyContent(GetValue(block, "content") ?? string.Empty))));
                }
                else if (GetString(block, "type") == "image")
                {
                    var source = ObjectValue(block, "source");
                    var url = AnthropicSourceToDataUrl(source);
                    if (!string.IsNullOrEmpty(url))
                    {
                        result.Add(Obj(
                            ("type", "image_url"),
                            ("image_url", Obj(("url", url)))));
                    }
                }
                else if (GetString(block, "type") == "document")
                {
                    var source = ObjectValue(block, "source");
                    var file = AnthropicDocumentSourceToChatFile(source);
                    if (file.Count > 0)
                    {
                        result.Add(Obj(("type", "file"), ("file", file)));
                    }
                }
                else
                {
                    result.Add(DeepCopy(block));
                }
            }

            if (result.Count == 1 && TryAsObject(result[0], out var single) && GetString(single, "type") == "text")
            {
                return GetValue(single, "text") ?? string.Empty;
            }

            return result;
        }

        return StringifyContent(content);
    }

    private static List<object?> ChatContentToAnthropicContent(object? content)
    {
        if (content is string text)
        {
            return string.IsNullOrEmpty(text) ? [] : [Obj(("type", "text"), ("text", text))];
        }

        if (TryAsList(content, out var blocks))
        {
            var result = new List<object?>();
            foreach (var item in blocks)
            {
                if (!TryAsObject(item, out var block))
                {
                    continue;
                }

                if (GetString(block, "type") is "text" or "input_text" or "output_text")
                {
                    var textValue = Convert.ToString(GetValue(block, "text")) ?? string.Empty;
                    if (!string.IsNullOrEmpty(textValue))
                    {
                        result.Add(Obj(("type", "text"), ("text", textValue)));
                    }
                }
                else if (GetString(block, "type") == "image_url")
                {
                    var imageUrl = ObjectValue(block, "image_url");
                    var url = GetString(imageUrl, "url") ?? string.Empty;
                    if (!string.IsNullOrEmpty(url))
                    {
                        result.Add(Obj(
                            ("type", "image"),
                            ("source", DataUrlOrUrlToAnthropicSource(url))));
                    }
                }
                else if (GetString(block, "type") == "input_image")
                {
                    var url = GetString(block, "image_url") ?? string.Empty;
                    if (!string.IsNullOrEmpty(url))
                    {
                        result.Add(Obj(
                            ("type", "image"),
                            ("source", DataUrlOrUrlToAnthropicSource(url))));
                    }
                }
                else if (GetString(block, "type") is "file" or "input_file")
                {
                    var file = GetString(block, "type") == "file" ? ObjectValue(block, "file") : block;
                    var source = ChatFileToAnthropicDocumentSource(file);
                    if (source.Count > 0)
                    {
                        var document = Obj(("type", "document"), ("source", source));
                        CopyIfPresent(file, document, "filename", "title");
                        result.Add(document);
                    }
                }
                else
                {
                    result.Add(DeepCopy(block));
                }
            }

            return result;
        }

        return [Obj(("type", "text"), ("text", StringifyContent(content)))];
    }

    private static bool IsEmptyChatContent(object? content)
    {
        if (content is null)
        {
            return true;
        }

        if (content is string text)
        {
            return text.Length == 0;
        }

        if (TryAsList(content, out var list))
        {
            return list.All(IsEmptyContentBlock);
        }

        return false;
    }

    private static bool IsEmptyContentBlock(object? block)
    {
        if (!TryAsObject(block, out var blockObject))
        {
            return false;
        }

        if (GetString(blockObject, "type") is "text" or "input_text" or "output_text")
        {
            return !IsTruthy(GetValue(blockObject, "text"));
        }

        if (blockObject.TryGetValue("content", out var content))
        {
            return IsEmptyChatContent(content);
        }

        if (blockObject.TryGetValue("text", out var text))
        {
            return !IsTruthy(text);
        }

        return false;
    }

    private static string StringifyContent(object? value)
    {
        value = NormalizeJsonValue(value);
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        if (TryAsList(value, out var list))
        {
            var parts = new List<string>();
            foreach (var item in list)
            {
                if (TryAsObject(item, out var itemObject))
                {
                    if (itemObject.TryGetValue("text", out var itemText))
                    {
                        parts.Add(Convert.ToString(itemText) ?? string.Empty);
                    }
                    else if (itemObject.TryGetValue("content", out var content))
                    {
                        parts.Add(StringifyContent(content));
                    }
                }
                else
                {
                    parts.Add(Convert.ToString(item) ?? string.Empty);
                }
            }

            return string.Concat(parts);
        }

        if (TryAsObject(value, out _))
        {
            return JsonDumps(value);
        }

        return Convert.ToString(value) ?? string.Empty;
    }

    private static void CopyIfPresent(
        Dictionary<string, object?> source,
        Dictionary<string, object?> target,
        string sourceKey,
        string? targetKey = null)
    {
        if (source.TryGetValue(sourceKey, out var value) && value is not null)
        {
            target[targetKey ?? sourceKey] = DeepCopy(value);
        }
    }

    private static Dictionary<string, object?> DataUrlOrUrlToAnthropicSource(string url)
    {
        if (TryParseDataUrl(url, out var mediaType, out var data))
        {
            return Obj(("type", "base64"), ("media_type", mediaType), ("data", data));
        }

        return Obj(("type", "url"), ("url", url));
    }

    private static string AnthropicSourceToDataUrl(Dictionary<string, object?> source)
    {
        var type = GetString(source, "type") ?? string.Empty;
        if (type == "url")
        {
            return GetString(source, "url") ?? string.Empty;
        }

        if (type == "base64")
        {
            var mediaType = GetString(source, "media_type") ?? "application/octet-stream";
            var data = GetString(source, "data") ?? string.Empty;
            return string.IsNullOrEmpty(data) ? string.Empty : $"data:{mediaType};base64,{data}";
        }

        return string.Empty;
    }

    private static Dictionary<string, object?> ChatFileToAnthropicDocumentSource(Dictionary<string, object?> file)
    {
        var fileData = GetString(file, "file_data") ?? string.Empty;
        if (!string.IsNullOrEmpty(fileData))
        {
            if (TryParseDataUrl(fileData, out var mediaType, out var data))
            {
                return Obj(("type", "base64"), ("media_type", mediaType), ("data", data));
            }

            return Obj(("type", "base64"), ("media_type", "application/pdf"), ("data", fileData));
        }

        var fileUrl = GetString(file, "file_url") ?? string.Empty;
        return string.IsNullOrEmpty(fileUrl) ? new Dictionary<string, object?>(StringComparer.Ordinal) : Obj(("type", "url"), ("url", fileUrl));
    }

    private static Dictionary<string, object?> AnthropicDocumentSourceToChatFile(Dictionary<string, object?> source)
    {
        var type = GetString(source, "type") ?? string.Empty;
        if (type == "url")
        {
            return Obj(("file_url", GetValue(source, "url")));
        }

        if (type == "base64")
        {
            var mediaType = GetString(source, "media_type") ?? "application/pdf";
            var data = GetString(source, "data") ?? string.Empty;
            return Obj(("file_data", $"data:{mediaType};base64,{data}"));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static bool TryParseDataUrl(string value, out string mediaType, out string data)
    {
        mediaType = string.Empty;
        data = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = value.IndexOf(',', StringComparison.Ordinal);
        if (separator <= 5)
        {
            return false;
        }

        var metadata = value[5..separator];
        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts.Skip(1).Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        mediaType = parts[0];
        data = value[(separator + 1)..];
        return data.Length > 0;
    }
}
