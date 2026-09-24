using System.Text;
using System.Text.Json;

namespace OpenCodex.Api.Infrastructure;

public sealed class RequestBodyReader : IRequestBodyReader
{
    private static readonly object RawBodyItemKey = new();

    public async Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await ReadBodyAsync(request, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>?)FromJsonElement(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ReadOnlyMemory<byte>? ReadCapturedRawBody(HttpRequest request)
    {
        return request.HttpContext.Items.TryGetValue(RawBodyItemKey, out var value)
            && value is ReadOnlyMemory<byte> captured
                ? captured
                : null;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // 按 Content-Length 预分配，避免 MemoryStream 扩容时的多次拷贝；
        // 只保留这一份 UTF-8 字节：既用于解析，也作为日志正文的原文来源。
        var capacity = request.ContentLength is > 0 and <= int.MaxValue
            ? (int)request.ContentLength.Value
            : 0;
        var buffer = new MemoryStream(capacity);
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var bytes = new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
        request.HttpContext.Items[RawBodyItemKey] = bytes;
        return bytes;
    }

    private static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NumberValue(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object NumberValue(JsonElement element)
    {
        if (!element.TryGetInt64(out var longValue))
        {
            return element.GetDouble();
        }

        if (longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return longValue;
    }
}
