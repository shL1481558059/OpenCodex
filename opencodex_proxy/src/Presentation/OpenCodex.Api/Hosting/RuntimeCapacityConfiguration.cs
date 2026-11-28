using OpenCodex.ProtocolConversion;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Hosting;

/// <summary>Only root capacities are runtime inputs. Old keys are migration errors, never fallbacks.</summary>
internal sealed class CapacityStartupValidation(SnapshotStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var minimum = checked(RequestExecutionBudget.MinimumExecutionCredits(store)
            + ClientConnectionCache.MinimumCommitCredits(store));
        if (store.MaxBytes < minimum)
            throw new InvalidOperationException($"SnapshotStoreMaxBytes cannot admit one standard request and its history commit. Required credits: {minimum}.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class RuntimeCapacityConfiguration
{
    internal static void Validate(IConfiguration configuration)
    {
        _ = ProtocolConversionParameters.Current.MaxRequestBodyBytes;
        _ = ProtocolConversionParameters.Current.SnapshotStoreMaxBytes;
        string[] deprecatedKeys =
        [
            "OpenCodex:MaxRequestBodyMb", "OPENCODEX_MAX_REQUEST_BODY_MB",
            "ProxyAuditSpool:MaxBodyBytes", "ProxyAuditSpool:MaxTotalBytes", "ProxyAuditSpool:MaxFiles"
        ];
        foreach (var key in deprecatedKeys)
            if (configuration[key] is not null)
                throw new InvalidOperationException(
                    $"Deprecated capacity setting '{key}'. Migrate capacity values to the .env keys with the ProtocolConversion__ prefix.");
        if (configuration.GetSection("MultiAgent:ClientWebSocketCache").Exists())
            throw new InvalidOperationException("Deprecated setting 'MultiAgent:ClientWebSocketCache'. Client history now uses the shared root snapshot budget.");
    }
}
