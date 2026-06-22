using System.Linq.Expressions;
using OpenCodex.Core.Domain;

namespace OpenCodex.CoreBase.Data;

/// <summary>
/// 实体仓储接口,供 Service 层访问数据,不依赖具体 ORM。
/// </summary>
/// <typeparam name="TEntity">实体类型</typeparam>
public interface IRepository<TEntity> where TEntity : BaseEntity
{
    /// <summary>可查询的实体集合(跟踪变更)。</summary>
    IQueryable<TEntity> Table { get; }

    /// <summary>可查询的实体集合(不跟踪变更,用于只读场景)。</summary>
    IQueryable<TEntity> TableNoTracking { get; }

    /// <summary>按主键获取实体。</summary>
    TEntity? GetById(object id);

    /// <summary>按主键异步获取实体。</summary>
    ValueTask<TEntity?> GetByIdAsync(object id);

    /// <summary>插入实体并保存。</summary>
    void Insert(TEntity entity);

    /// <summary>异步插入实体并保存。</summary>
    Task InsertAsync(TEntity entity);

    /// <summary>批量插入实体并保存。</summary>
    void Insert(IEnumerable<TEntity> entities);

    /// <summary>异步批量插入实体并保存。</summary>
    Task InsertAsync(IEnumerable<TEntity> entities);

    /// <summary>更新实体并保存。</summary>
    void Update(TEntity entity);

    /// <summary>异步更新实体并保存。</summary>
    Task UpdateAsync(TEntity entity);

    /// <summary>更新实体指定属性并保存。</summary>
    void Update(TEntity entity, params string[] propNames);

    /// <summary>异步更新实体指定属性并保存。</summary>
    Task UpdateAsync(TEntity entity, params string[] propNames);

    /// <summary>删除实体并保存。</summary>
    void Delete(TEntity entity);

    /// <summary>异步删除实体并保存。</summary>
    Task DeleteAsync(TEntity entity);

    /// <summary>批量删除实体并保存。</summary>
    void Delete(IEnumerable<TEntity> entities);

    /// <summary>批量删除所有实体（直接执行 DELETE FROM，不加载实体到内存）。</summary>
    /// <returns>受影响的行数。</returns>
    int ExecuteDeleteAll();

    /// <summary>异步批量删除实体并保存。</summary>
    Task DeleteAsync(IEnumerable<TEntity> entities);

    /// <summary>保存所有变更。</summary>
    int SaveChanges();

    /// <summary>异步保存所有变更。</summary>
    Task<int> SaveChangesAsync();
}
