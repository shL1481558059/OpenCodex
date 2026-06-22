using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Data;

/// <summary>
/// 基于 EF Core 的仓储实现。
/// </summary>
/// <typeparam name="TEntity">实体类型</typeparam>
public class EfRepository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly OpenCodexDbContext _context;
    private DbSet<TEntity>? _entities;

    public EfRepository(OpenCodexDbContext context)
    {
        _context = context;
    }

    protected DbSet<TEntity> Entities => _entities ??= _context.Set<TEntity>();

    public IQueryable<TEntity> Table => Entities;

    public IQueryable<TEntity> TableNoTracking => Entities.AsNoTracking();

    public TEntity? GetById(object id) => Entities.Find(id);

    public async ValueTask<TEntity?> GetByIdAsync(object id) => await Entities.FindAsync(id);

    public void Insert(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        Entities.Add(entity);
        _context.SaveChanges();
    }

    public async Task InsertAsync(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        await Entities.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    public void Insert(IEnumerable<TEntity> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));
        Entities.AddRange(entities);
        _context.SaveChanges();
    }

    public async Task InsertAsync(IEnumerable<TEntity> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));
        await Entities.AddRangeAsync(entities);
        await _context.SaveChangesAsync();
    }

    public void Update(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        Entities.Update(entity);
        _context.SaveChanges();
    }

    public async Task UpdateAsync(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        Entities.Update(entity);
        await _context.SaveChangesAsync();
    }

    public void Update(TEntity entity, params string[] propNames)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        var entry = Entities.Attach(entity);
        foreach (var name in propNames)
        {
            entry.Property(name).IsModified = true;
        }
        _context.SaveChanges();
    }

    public async Task UpdateAsync(TEntity entity, params string[] propNames)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        var entry = Entities.Attach(entity);
        foreach (var name in propNames)
        {
            entry.Property(name).IsModified = true;
        }
        await _context.SaveChangesAsync();
    }

    public void Delete(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        Entities.Remove(entity);
        _context.SaveChanges();
    }

    public async Task DeleteAsync(TEntity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        Entities.Remove(entity);
        await _context.SaveChangesAsync();
    }

    public void Delete(IEnumerable<TEntity> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));
        Entities.RemoveRange(entities);
        _context.SaveChanges();
    }

    public int ExecuteDeleteAll()
    {
        return Entities.ExecuteDelete();
    }

    public async Task DeleteAsync(IEnumerable<TEntity> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));
        Entities.RemoveRange(entities);
        await _context.SaveChangesAsync();
    }

    public int SaveChanges() => _context.SaveChanges();

    public Task<int> SaveChangesAsync() => _context.SaveChangesAsync();
}
