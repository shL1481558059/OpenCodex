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

    DbSet<ModelProvider> ModelProviders { get; }

    DbSet<ModelInfo> ModelInfos { get; }

    DbSet<ChannelModelInfo> ChannelModelInfos { get; }

    DbSet<VisionTransferSettings> VisionTransferSettings { get; }
    DbSet<ModelPricingPlan> ModelPricingPlans { get; }

    DbSet<ModelPricingRule> ModelPricingRules { get; }

    DbSet<ChannelModelMapping> ChannelModelMappings { get; }

    DbSet<RequestLog> RequestLogs { get; }

    DbSet<LogContentBlock> LogContentBlocks { get; }

    DbSet<LogContentManifest> LogContentManifests { get; }

    DbSet<LogContentManifestChunk> LogContentManifestChunks { get; }

    DbSet<RequestLogContentRef> RequestLogContentRefs { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    EntityEntry Entry(object entity);

    int SaveChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
