namespace OpenCodex.Api.Services;

public sealed record ProxyEndpointResult(
    int StatusCode,
    object? Payload,
    bool IsEmpty);
