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

}
