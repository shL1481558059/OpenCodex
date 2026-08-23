using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

public interface IModelCatalogService
{
    ApiOpResult<ModelProviderListResponse> ListProviders(bool includeDisabled = false);

    ApiOpResult<ModelProviderResponsePayload> CreateProvider(ModelProviderUpsertRequest request);

    ApiOpResult<ModelProviderResponsePayload> UpdateProvider(Guid id, ModelProviderUpsertRequest request);

    ApiOpResult<ModelProviderResponsePayload> DeleteProvider(Guid id);

    ApiOpResult<ModelInfoListResponse> ListModels(
        string? query,
        string? providerCode,
        bool? enabled);

    ApiOpResult<ModelInfoResponsePayload> ReadModelInfoById(Guid id);

    ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request);

    ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request);

    ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id);

    ApiOpResult<ModelCatalogTransferDocument> ExportModelCatalog();

    ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(ModelCatalogTransferDocument document, bool dryRun);

    ApiOpResult<ChannelModelInfoListResponse> ListChannelModelInfos(Guid channelId);

    ApiOpResult<ChannelModelInfoResponsePayload> UpsertChannelModelInfo(
        Guid channelId,
        ChannelModelInfoUpsertRequest request);

    ApiOpResult DeleteChannelModelInfo(Guid channelId, Guid id);

    bool SupportsImage(Guid? channelId, string? upstreamModel, bool legacyMappingValue);

   Task<ModelPricingCalculationResult> CalculateCostAsync(
       Guid? channelId,
       string? requestModel,
       string? upstreamModel,
       ModelUsageVector usage);
}
