using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
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

    /// <summary>
    /// 读取模型下拉选项，供日志筛选等轻量场景使用；以 ModelKey 作为选项值。
    /// </summary>
    /// <param name="query">按模型 key 或显示名称过滤的可选关键字。</param>
    ApiOpResult<IReadOnlyList<SelectOption<string>>> ListModelSelectOptions(string? query);

    IReadOnlyList<Dictionary<string, object?>> BuildProxyModelCatalog(
        IReadOnlyList<ProxyModelCapabilityDto> routedModels);

    ApiOpResult<ModelInfoResponsePayload> ReadModelInfoById(Guid id);

    ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request);

    ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request);

    ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id);

    ApiOpResult<ModelBatchActionResult> BatchModels(
        ModelBatchActionRequest request);

    ApiOpResult<ModelCatalogTransferDocument> ExportModelCatalog();

    ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(ModelCatalogTransferDocument document, bool dryRun);

    ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(
        ModelCatalogTransferDocument document,
        bool dryRun,
        ModelCatalogImportOptions options);

    ApiOpResult<ChannelModelInfoListResponse> ListChannelModelInfos(Guid channelId);

    ApiOpResult<ChannelModelInfoResponsePayload> UpsertChannelModelInfo(
        Guid channelId,
        ChannelModelInfoUpsertRequest request);

    ApiOpResult DeleteChannelModelInfo(Guid channelId, Guid id);

    bool SupportsImage(Guid? channelId, string? upstreamModel);

   Task<ModelPricingCalculationResult> CalculateCostAsync(
       Guid? channelId,
       string? requestModel,
       string? upstreamModel,
       ModelUsageVector usage,
       DateTimeOffset billingInstant);
}
