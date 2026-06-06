using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services;

public sealed record AdminWebSearchTestResult(
    TavilyKeyRecord Key,
    WebSearchProviderResult Result,
    WebSearchConfigRecord Config);
