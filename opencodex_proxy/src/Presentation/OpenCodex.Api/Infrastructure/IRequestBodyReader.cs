using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

public interface IRequestBodyReader
{
    async Task<JsonPayload?> ReadJsonPayloadAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = await ReadJsonObjectAsync(request, cancellationToken);
        return payload is null
            ? null
            : JsonPayload.FromOwnedUtf8(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default);
}
