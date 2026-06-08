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
}
