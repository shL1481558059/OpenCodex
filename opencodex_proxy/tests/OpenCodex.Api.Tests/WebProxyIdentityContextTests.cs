using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenCodex.Api.Authentication;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebProxyIdentityContextTests
{
    [Fact]
    public void Current_ProxyBearerPrincipal_ReturnsIdentity()
    {
        var apiKeyId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var context = CreateContext(CreateProxyPrincipal(apiKeyId, ownerUserId, "alice", "user"));
        var identityContext = new WebProxyIdentityContext(context);

        var identity = identityContext.Current;

        Assert.NotNull(identity);
        Assert.Equal(apiKeyId, identity!.ApiKeyId);
        Assert.Equal(ownerUserId, identity.OwnerUserId);
        Assert.Equal("alice", identity.OwnerUsername);
        Assert.Equal("user", identity.OwnerRole);
        Assert.True(identityContext.IsAuthenticated);
    }

    [Fact]
    public void Current_AdminCookiePrincipal_ReturnsNull()
    {
        var claims = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "superadmin"),
                new Claim("opencodex_admin_user_id", Guid.NewGuid().ToString("D")),
                new Claim("opencodex_admin_enabled", "true")
            ],
            "OpenCodexAdmin");
        var context = CreateContext(new ClaimsPrincipal(claims));
        var identityContext = new WebProxyIdentityContext(context);

        Assert.Null(identityContext.Current);
        Assert.False(identityContext.IsAuthenticated);
    }

    [Theory]
    [InlineData(ProxyIdentityClaims.ApiKeyId, "not-a-guid", ProxyIdentityClaims.OwnerUserId, "00000000-0000-0000-0000-000000000001", "alice", "user")]
    [InlineData(ProxyIdentityClaims.ApiKeyId, "00000000-0000-0000-0000-000000000001", ProxyIdentityClaims.OwnerUserId, "not-a-guid", "alice", "user")]
    [InlineData(ProxyIdentityClaims.ApiKeyId, "00000000-0000-0000-0000-000000000001", ProxyIdentityClaims.OwnerUserId, "00000000-0000-0000-0000-000000000002", "", "user")]
    [InlineData(ProxyIdentityClaims.ApiKeyId, "00000000-0000-0000-0000-000000000001", ProxyIdentityClaims.OwnerUserId, "00000000-0000-0000-0000-000000000002", "alice", "")]
    public void Current_IncompleteProxyClaims_ReturnsNull(
        string apiKeyClaim,
        string apiKeyValue,
        string ownerClaim,
        string ownerValue,
        string username,
        string role)
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(username))
        {
            claims.Add(new Claim(ClaimTypes.Name, username));
        }

        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ProxyIdentityClaims.OwnerRole, role));
        }

        claims.Add(new Claim(apiKeyClaim, apiKeyValue));
        claims.Add(new Claim(ownerClaim, ownerValue));

        var context = CreateContext(
            new ClaimsPrincipal(new ClaimsIdentity(claims, ProxyBearerAuthenticationDefaults.Scheme)));
        var identityContext = new WebProxyIdentityContext(context);

        Assert.Null(identityContext.Current);
    }

    [Fact]
    public void RequireIdentity_WithoutHttpContext_ThrowsUnauthorized()
    {
        var identityContext = new WebProxyIdentityContext(new HttpContextAccessor());

        var exception = Assert.Throws<BadRequestException>(() => identityContext.RequireIdentity());

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
        Assert.Equal(
            ProxyBearerAuthenticationDefaults.InvalidCredentialMessage,
            exception.Message);
    }

    [Fact]
    public void RequireIdentity_AnonymousRequest_ThrowsUnauthorized()
    {
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));
        var identityContext = new WebProxyIdentityContext(context);

        Assert.Throws<BadRequestException>(() => identityContext.RequireIdentity());
    }

    private static IHttpContextAccessor CreateContext(ClaimsPrincipal principal)
    {
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static ClaimsPrincipal CreateProxyPrincipal(
        Guid apiKeyId,
        Guid ownerUserId,
        string ownerUsername,
        string ownerRole)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, ownerUsername),
                new Claim(ProxyIdentityClaims.ApiKeyId, apiKeyId.ToString("D")),
                new Claim(ProxyIdentityClaims.OwnerUserId, ownerUserId.ToString("D")),
                new Claim(ProxyIdentityClaims.OwnerRole, ownerRole)
            ],
            ProxyBearerAuthenticationDefaults.Scheme);
        return new ClaimsPrincipal(identity);
    }
}
