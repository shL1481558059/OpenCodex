using OpenCodex.CoreBase.DTOs.AdminChannelDiagnostics;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminChannelDiagnosticsService
{
    Task<ApiResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken);

    Task<ApiResult<TestChannelResponse>> TestChannelAsync(
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken);
}
