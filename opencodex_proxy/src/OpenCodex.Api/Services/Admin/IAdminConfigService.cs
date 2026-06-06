using OpenCodex.Api.Domain;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public interface IAdminConfigService
{
    ServiceResult<IReadOnlyList<ChannelRecord>> ReadConfig(
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<IReadOnlyList<ChannelRecord>> SaveConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<AdminConfigImportResult> ImportConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin);
}
