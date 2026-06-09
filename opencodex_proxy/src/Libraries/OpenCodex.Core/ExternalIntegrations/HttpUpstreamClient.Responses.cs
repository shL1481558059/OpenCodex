using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    private static async Task<Dictionary<string, object?>> ReadJsonObject(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var value = FromJsonElement(document.RootElement);
            if (value is Dictionary<string, object?> dictionary)
            {
                return dictionary;
            }
        }
        catch (JsonException)
        {
            throw new UpstreamException(
                "upstream returned invalid JSON",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }

        throw new UpstreamException(
            "upstream returned invalid JSON",
            ProxyHttpStatus.BadGateway,
            channelId: JsonDictionaryValue.String(channel, "id"));
    }

    private static async Task<Dictionary<string, object?>> ReadJsonModelList(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var value = FromJsonElement(document.RootElement);
            return value switch
            {
                Dictionary<string, object?> dictionary => dictionary,
                List<object?> list => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["data"] = list
                },
                _ => throw new UpstreamException(
                    "upstream returned invalid JSON",
                    ProxyHttpStatus.BadGateway,
                    channelId: JsonDictionaryValue.String(channel, "id"))
            };
        }
        catch (JsonException)
        {
            throw new UpstreamException(
                "upstream returned invalid JSON",
                ProxyHttpStatus.BadGateway,
                channelId: JsonDictionaryValue.String(channel, "id"));
        }
    }

    private static async Task ThrowHttpError(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, object?> channel,
        CancellationToken cancellationToken)
    {
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new UpstreamException(
            $"upstream returned HTTP {(int)response.StatusCode}",
            (int)response.StatusCode,
            DecodeBody(bodyText),
            JsonDictionaryValue.String(channel, "id"));
    }

    private static object? DecodeBody(string bodyText)
    {
        if (bodyText.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyText);
            return FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return bodyText.Length <= 2000 ? bodyText : bodyText[..2000];
        }
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
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
