using Microsoft.Data.Sqlite;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public sealed class SqliteRepository<TEntity> : IRepository<TEntity>
    where TEntity : BaseEntity
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public SqliteRepository(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public TEntity? GetById(object id)
    {
        var settings = _settingsProvider.GetSettings();
        OpenCodexDatabase.Initialize(settings.DbPath, settings.AdminUsername);
        using var connection = OpenCodexDatabase.OpenRepositoryConnection(settings.DbPath);
        return SqliteEntityMap<TEntity>.GetById(connection, id);
    }

    public IReadOnlyList<TEntity> ListAll()
    {
        var settings = _settingsProvider.GetSettings();
        OpenCodexDatabase.Initialize(settings.DbPath, settings.AdminUsername);
        using var connection = OpenCodexDatabase.OpenRepositoryConnection(settings.DbPath);
        return SqliteEntityMap<TEntity>.ListAll(connection);
    }
}
