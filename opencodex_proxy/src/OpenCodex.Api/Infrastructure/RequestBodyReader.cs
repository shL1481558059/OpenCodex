using System.Text.Json;

namespace OpenCodex.Api.Infrastructure;

public sealed class RequestBodyReader : IRequestBodyReader
{
    public async Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>?)FromJsonElement(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<Dictionary<string, object?>> ReadFormOrJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            return form.ToDictionary(
                item => item.Key,
                item => (object?)item.Value.ToString(),
                StringComparer.Ordinal);
        }

        return await ReadJsonObjectAsync(request, cancellationToken) ?? [];
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
