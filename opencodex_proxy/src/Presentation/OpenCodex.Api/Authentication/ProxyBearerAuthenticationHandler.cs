using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenCodex.Api.Errors;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Authentication;

/// <summary>
/// 使用请求凭据认证代理端点访问密钥，并把身份写入 <see cref="HttpContext.User"/>。
/// </summary>
public sealed class ProxyBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly IProxyAccessService _access;

    public ProxyBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IProxyAccessService access)
        : base(options, logger, encoder)
    {
        _access = access;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ReadRawKey(Request);
        if (rawKey is null)
        {
            return AuthenticateResult.NoResult();
        }

        var accessKey = await _access.AuthenticateRawKeyAsync(rawKey);
        if (accessKey is null)
        {
            return AuthenticateResult.Fail(ProxyBearerAuthenticationDefaults.InvalidCredentialMessage);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, accessKey.OwnerUsername),
                new Claim(ProxyIdentityClaims.ApiKeyId, accessKey.Id.ToString("D")),
                new Claim(ProxyIdentityClaims.OwnerUserId, accessKey.OwnerUserId.ToString("D")),
                new Claim(ProxyIdentityClaims.OwnerRole, accessKey.User.Role)
            ],
            ProxyBearerAuthenticationDefaults.Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await ProxyErrorResponseWriter.WriteAsync(
            Context,
            new BadRequestException(
                ProxyBearerAuthenticationDefaults.InvalidCredentialMessage,
                StatusCodes.Status401Unauthorized));
    }

    private static string? ReadRawKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var values))
        {
            return null;
        }

        var header = values.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rawKey = header[BearerPrefix.Length..].Trim();
        return rawKey.Length == 0 ? null : rawKey;
    }
}
