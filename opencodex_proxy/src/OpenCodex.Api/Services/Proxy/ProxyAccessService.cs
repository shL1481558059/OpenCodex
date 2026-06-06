using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;

namespace OpenCodex.Api.Services;

public sealed class ProxyAccessService : IProxyAccessService
{
    private const string RequiredBearerMessage = "valid bearer api key required";

    private readonly IProxyAccessRepository _access;

    public ProxyAccessService(IProxyAccessRepository access)
    {
        _access = access;
    }

    public AuthenticatedAccessApiKeyRecord AuthenticateBearer(string? authorizationHeader)
    {
        const string prefix = "Bearer ";
        var authorization = authorizationHeader ?? string.Empty;
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Unauthorized();
        }

        var rawKey = authorization[prefix.Length..].Trim();
        var accessKey = _access.AuthenticateAccessApiKey(rawKey);
        if (accessKey is null)
        {
            throw Unauthorized();
        }

        return accessKey;
    }

    private static BadRequestException Unauthorized()
    {
        return new BadRequestException(RequiredBearerMessage, ProxyHttpStatus.Unauthorized);
    }
}
