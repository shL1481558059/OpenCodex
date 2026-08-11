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
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            request.HttpContext.Items[RawBodyItemKey] = new UTF8Encoding(false, true).GetString(bytes);
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

    internal static string? ReadCapturedRawBody(HttpRequest request)
    {
        return request.HttpContext.Items.TryGetValue(RawBodyItemKey, out var value)
            ? value as string
            : null;
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
