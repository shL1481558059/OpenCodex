using System.Security.Cryptography;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyRequestService : IProxyRequestService
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ProxyRequestService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public ProxyRequestState StartRequest()
    {
        var settings = _settingsProvider.GetSettings();
        return new ProxyRequestState(
            RandomNumberGenerator.GetHexString(12).ToLowerInvariant(),
            settings.DefaultTimeout);
    }
}
