using System.Security.Cryptography;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services;

public sealed class ProxyRequestService : IProxyRequestService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly IProxyAccessService _access;

    public ProxyRequestService(
        IOpenCodexRuntimeSettingsProvider settingsProvider,
        IProxyAccessService access)
    {
        _settingsProvider = settingsProvider;
        _access = access;
    }

    public ProxyRequestState StartRequest()
    {
        var settings = _settingsProvider.GetSettings();
        return new ProxyRequestState(
            RandomNumberGenerator.GetHexString(12).ToLowerInvariant(),
            settings.AdminUsername,
            settings.DefaultTimeout);
    }

    public AuthenticatedAccessApiKeyRecord AuthenticateAccessKey(string? authorizationHeader)
    {
        return _access.AuthenticateBearer(authorizationHeader);
    }
}
