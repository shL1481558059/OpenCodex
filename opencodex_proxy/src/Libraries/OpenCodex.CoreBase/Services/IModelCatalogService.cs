using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

public interface IModelCatalogService
{
    ApiOpResult<ModelProviderListResponse> ListProviders(bool includeDisabled = false);

    ApiOpResult<ModelProviderResponsePayload> CreateProvider(ModelProviderUpsertRequest request);

    ApiOpResult<ModelInfoListResponse> ListModels(
        string? query,
        string? providerCode,
        bool? enabled);

    ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request);

    ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request);

    ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id);

    ApiOpResult<ChannelModelInfoListResponse> ListChannelModelInfos(Guid channelId);

    ApiOpResult<ChannelModelInfoResponsePayload> UpsertChannelModelInfo(
        Guid channelId,
        ChannelModelInfoUpsertRequest request);

    ApiOpResult RestoreChannelModelInfo(Guid channelId, Guid id);

    bool SupportsImage(Guid? channelId, string? upstreamModel, bool legacyMappingValue);

    ApiOpResult<SeedModelCatalogResponse> SeedDefaults();

    ModelPricingCalculationResult CalculateCost(
        Guid? channelId,
        string? requestModel,
        string? upstreamModel,
        string? responseModel,
        ModelUsageVector usage);
}
