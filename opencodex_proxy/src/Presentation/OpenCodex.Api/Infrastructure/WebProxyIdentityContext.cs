using System.Security.Claims;
using OpenCodex.Api.Authentication;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Infrastructure;

public sealed class WebProxyIdentityContext : IProxyIdentityContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public WebProxyIdentityContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAuthenticated => Current is not null;

    public ProxyIdentity? Current
    {
        get
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            var identity = principal?.Identities.FirstOrDefault(item => string.Equals(
                item.AuthenticationType,
                ProxyBearerAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));
            if (identity is null)
            {
                return null;
            }

            var ownerUsername = identity.FindFirst(ClaimTypes.Name)?.Value;
            var ownerRole = identity.FindFirst(ProxyIdentityClaims.OwnerRole)?.Value;
            if (!Guid.TryParse(
                    identity.FindFirst(ProxyIdentityClaims.ApiKeyId)?.Value,
                    out var apiKeyId)
                || !Guid.TryParse(
                    identity.FindFirst(ProxyIdentityClaims.OwnerUserId)?.Value,
                    out var ownerUserId)
                || string.IsNullOrWhiteSpace(ownerUsername)
                || string.IsNullOrWhiteSpace(ownerRole))
            {
                return null;
            }

            return new ProxyIdentity(
                apiKeyId,
                ownerUserId,
                ownerUsername.Trim(),
                ownerRole.Trim());
        }
    }

    public ProxyIdentity RequireIdentity()
    {
        return Current ?? throw Unauthorized();
    }

    private static BadRequestException Unauthorized()
    {
        return new BadRequestException(
            ProxyBearerAuthenticationDefaults.InvalidCredentialMessage,
            StatusCodes.Status401Unauthorized);
    }
}
