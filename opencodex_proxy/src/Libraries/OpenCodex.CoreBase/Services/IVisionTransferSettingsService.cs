using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

public interface IVisionTransferSettingsService
{
    ApiOpResult<VisionTransferSettingsResponse> Read(string? ownerUsername);

    ApiOpResult<VisionTransferCandidateListResponse> ListCandidates(string? ownerUsername);

    ApiOpResult<VisionTransferSettingsResponse> Save(VisionTransferSettingsUpdateRequest request);

    ApiOpResult Delete(string? ownerUsername);

    VisionTransferSettingsSnapshot? GetSnapshot(Guid ownerUserId);
}
