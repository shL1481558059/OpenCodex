using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

public interface IModelCatalogService
{
    ApiOpResult<ModelProviderListResponse> ListProviders(bool includeDisabled = false);

    ApiOpResult<ModelInfoListResponse> ListModels(
        string? query,
        string? providerCode,
        string? scope,
        bool? enabled,
        Guid? channelId);

    ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request);

    ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request);

    ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id);

    ApiOpResult<SeedModelCatalogResponse> SeedDefaults();

    ModelPricingCalculationResult CalculateCost(
        Guid? channelId,
        string? requestModel,
        string? upstreamModel,
        string? responseModel,
        ModelUsageVector usage);
}
