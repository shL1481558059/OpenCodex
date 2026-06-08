using System.Security.Cryptography;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

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

    public AuthenticatedAccessApiKeyDto AuthenticateAccessKey(string? authorizationHeader)
    {
        return _access.AuthenticateBearer(authorizationHeader);
    }
}
