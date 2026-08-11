using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.DTOs;

namespace OpenCodex.Core.Services.Mapping;

public static class EntityDtoExtensions
{
    public static UserDto ToDto(this User source) => new(
        source.Id,
        source.Username,
        source.Role,
        source.Enabled,
        source.CreatedAt,
        source.UpdatedAt);

    public static ModelPricingDto ToDto(this ModelPricing source) => new(
        source.Id,
        source.ModelId,
        source.Vendor,
        source.Name,
        source.MatchPattern,
        source.InputPrice,
        source.CachedInputPrice,
        source.OutputPrice,
        source.Enabled,
        source.Source,
        source.CreatedAt,
        source.UpdatedAt);
}
