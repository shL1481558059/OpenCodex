using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Pricing;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services;

/// <summary>
/// 定义模型定价管理与计费计算服务。
/// </summary>
public interface IModelPricingService
{
    ApiOpResult<ModelPricingListResponse> ListPrices(
        string? query,
        string? vendor,
        bool? enabled);

    ApiOpResult<ModelPricingResponsePayload> CreatePrice(
        ModelPricingCreateCommand command);

    ApiOpResult<ModelPricingResponsePayload> UpdatePrice(
        long id,
        ModelPricingUpdateCommand command);

    ApiOpResult<DeleteModelPricingResponse> DeletePrice(
        long id);

    Task<ApiOpResult<SeedModelPricingResponse>> SeedDefaultsAsync(
        CancellationToken cancellationToken = default);

    double CalculateCost(
        string model,
        int inputTokens,
        int cachedTokens,
        int outputTokens);
}
