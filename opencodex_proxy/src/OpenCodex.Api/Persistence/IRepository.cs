using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IRepository<TEntity>
    where TEntity : BaseEntity
{
    TEntity? GetById(object id);

    IReadOnlyList<TEntity> ListAll();
}
