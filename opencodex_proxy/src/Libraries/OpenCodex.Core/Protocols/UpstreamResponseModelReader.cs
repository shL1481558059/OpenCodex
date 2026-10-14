namespace OpenCodex.Core.Protocols;

/// <summary>
/// 从上游响应（原始 JSON 或流式重建体）读取模型 id。
/// 不读取返回给客户端的响应：那里的 model 会被改写成入站别名。
/// </summary>
internal static class UpstreamResponseModelReader
{
    public static string? Read(IReadOnlyDictionary<string, object?>? response)
    {
        if (response is null)
        {
            return null;
        }

        if (TryObject(response, "data", out var data) && IsChatEnvelope(response, data))
        {
            var nested = ModelId(data);
            if (nested is not null)
            {
                return nested;
            }
        }

        var model = ModelId(response);
        if (model is not null)
        {
            return model;
        }

        if (TryObject(response, "response", out var nestedResponse))
        {
            model = ModelId(nestedResponse);
            if (model is not null)
            {
                return model;
            }
        }

        if (TryObject(response, "message", out var message))
        {
            return ModelId(message);
        }

        return null;
    }

    private static bool IsChatEnvelope(
        IReadOnlyDictionary<string, object?> response,
        IReadOnlyDictionary<string, object?> data)
    {
        if (response.TryGetValue("success", out var success) && success is bool)
        {
            return true;
        }

        if (data.ContainsKey("choices"))
        {
            return true;
        }

        return data.TryGetValue("object", out var objectValue)
            && objectValue is "chat.completion" or "chat.completion.chunk";
    }

    private static string? ModelId(IReadOnlyDictionary<string, object?> value)
    {
        if (!value.TryGetValue("model", out var model) || model is not string text)
        {
            return null;
        }

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool TryObject(
        IReadOnlyDictionary<string, object?> value,
        string key,
        out IReadOnlyDictionary<string, object?> result)
    {
        if (value.TryGetValue(key, out var item) && item is IReadOnlyDictionary<string, object?> dictionary)
        {
            result = dictionary;
            return true;
        }

        result = null!;
        return false;
    }
}
