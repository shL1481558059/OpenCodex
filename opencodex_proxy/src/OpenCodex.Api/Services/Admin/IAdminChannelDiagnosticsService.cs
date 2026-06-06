namespace OpenCodex.Api.Services;

public interface IAdminChannelDiagnosticsService
{
    Task<AdminDiscoverModelsResult> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken);

    Task<AdminChannelTestResult> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken);
}
