namespace OpenCodex.Api.Infrastructure;

public interface IRequestBodyReader
{
    Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object?>> ReadFormOrJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default);
}
