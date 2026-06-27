using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenCodex.Core.Domain;

namespace OpenCodex.CoreBase.Data;

public interface IOpenCodexDbContext : IDisposable, IAsyncDisposable
{
    DbSet<User> Users { get; }

    DbSet<Channel> Channels { get; }

    DbSet<AccessApiKey> AccessApiKeys { get; }

    DbSet<WebSearchSettings> WebSearchSettings { get; }

    DbSet<TavilyKey> TavilyKeys { get; }

    DbSet<ModelPricing> ModelPricings { get; }

    DbSet<ModelProvider> ModelProviders { get; }

    DbSet<ModelInfo> ModelInfos { get; }

    DbSet<ModelPricingPlan> ModelPricingPlans { get; }

    DbSet<ModelPricingRule> ModelPricingRules { get; }

    DbSet<ChannelModelMapping> ChannelModelMappings { get; }

    DbSet<RequestLog> RequestLogs { get; }

    DbSet<RequestLogDetail> RequestLogDetails { get; }

    DbSet<RequestLogStreamLine> RequestLogStreamLines { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    EntityEntry Entry(object entity);

    int SaveChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
