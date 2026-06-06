namespace OpenCodex.Api.Services;

public sealed record ProxyNonStreamResult(
    int StatusCode,
    object? Payload);
