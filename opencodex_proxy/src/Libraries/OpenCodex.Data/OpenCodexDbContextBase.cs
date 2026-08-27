using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Data;

namespace OpenCodex.Data;

public abstract class OpenCodexDbContextBase : DbContext, IOpenCodexDbContext
{
    protected OpenCodexDbContextBase(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<AccessApiKey> AccessApiKeys => Set<AccessApiKey>();

    public DbSet<WebSearchSettings> WebSearchSettings => Set<WebSearchSettings>();

    public DbSet<TavilyKey> TavilyKeys => Set<TavilyKey>();

    public DbSet<ModelProvider> ModelProviders => Set<ModelProvider>();

    public DbSet<ModelInfo> ModelInfos => Set<ModelInfo>();

    public DbSet<ChannelModelInfo> ChannelModelInfos => Set<ChannelModelInfo>();

    public DbSet<ModelPricingPlan> ModelPricingPlans => Set<ModelPricingPlan>();

    public DbSet<ModelPricingRule> ModelPricingRules => Set<ModelPricingRule>();

    public DbSet<ChannelModelMapping> ChannelModelMappings => Set<ChannelModelMapping>();

    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    public DbSet<LogContentBlock> LogContentBlocks => Set<LogContentBlock>();

    public DbSet<LogContentManifest> LogContentManifests => Set<LogContentManifest>();

    public DbSet<LogContentManifestChunk> LogContentManifestChunks => Set<LogContentManifestChunk>();

    public DbSet<RequestLogContentRef> RequestLogContentRefs => Set<RequestLogContentRef>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUsers(modelBuilder);
        ConfigureChannels(modelBuilder);
        ConfigureAccessApiKeys(modelBuilder);
        ConfigureWebSearch(modelBuilder);
        ConfigureModelCatalog(modelBuilder);
        ConfigureRequestLogs(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<User>();
        entity.ToTable("Users");
        entity.HasKey(user => user.Id);
        entity.Property(user => user.Id).ValueGeneratedOnAdd();
        entity.Property(user => user.Username).IsRequired();
        entity.HasIndex(user => user.Username).IsUnique();
        entity.Property(user => user.PasswordHash).IsRequired();
        entity.Property(user => user.Role).IsRequired();
    }

    private static void ConfigureChannels(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Channel>();
        entity.ToTable("Channels");
        entity.HasKey(channel => channel.Id);
        entity.Property(channel => channel.Id).ValueGeneratedOnAdd();
        entity.Property(channel => channel.Name).IsRequired();
        entity.Property(channel => channel.GroupName).IsRequired();
        entity.Property(channel => channel.Type).IsRequired();
        entity.Property(channel => channel.BaseUrl).IsRequired();
        entity.Property(channel => channel.ApiKey).IsRequired();
        entity.Property(channel => channel.AuthMode).IsRequired();
        entity.Property(channel => channel.HeadersJson).IsRequired();
        entity.Property(channel => channel.CircuitBreakDurationSeconds).IsRequired();
        entity.Property(channel => channel.Capacity).IsRequired();
        entity.Property(channel => channel.CompatJson).IsRequired();
        entity.Property(channel => channel.ModelsJson).IsRequired();
        entity.HasIndex(channel => new { channel.OwnerUserId, channel.Position });
        entity.HasIndex(channel => new { channel.OwnerUserId, channel.Priority, channel.Position });
    }

    private static void ConfigureAccessApiKeys(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AccessApiKey>();
        entity.ToTable("AccessApiKeys");
        entity.HasKey(key => key.Id);
        entity.Property(key => key.Id).ValueGeneratedOnAdd();
        entity.Property(key => key.Name).IsRequired();
        entity.Property(key => key.OwnerUserId).IsRequired();
        entity.Property(key => key.KeyHash).IsRequired();
        entity.Property(key => key.KeyPrefix).IsRequired();
        entity.Property(key => key.KeySuffix).IsRequired();
        entity.HasIndex(key => key.KeyHash).IsUnique();
        entity.HasIndex(key => new { key.OwnerUserId, key.Id });
    }

    private static void ConfigureWebSearch(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<WebSearchSettings>();
        settings.ToTable("WebSearchSettings");
        settings.HasKey(item => item.Id);
        settings.Property(item => item.Id).ValueGeneratedOnAdd();
        settings.Property(item => item.Mode).IsRequired();

        var keys = modelBuilder.Entity<TavilyKey>();
        keys.ToTable("TavilyKeys");
        keys.HasKey(key => key.Id);
        keys.Property(key => key.Id).ValueGeneratedOnAdd();
        keys.Property(key => key.Provider).IsRequired();
        keys.Property(key => key.ApiKey).IsRequired();
        keys.HasIndex(key => key.Position);
    }

    private static void ConfigureModelCatalog(ModelBuilder modelBuilder)
    {
        var providers = modelBuilder.Entity<ModelProvider>();
        providers.ToTable("ModelProviders");
        providers.HasKey(provider => provider.Id);
        providers.Property(provider => provider.Id).ValueGeneratedOnAdd();
        providers.Property(provider => provider.Code).IsRequired();
        providers.Property(provider => provider.Name).IsRequired();
        providers.Property(provider => provider.Source).IsRequired();
        providers.HasIndex(provider => provider.Code).IsUnique();
        providers.HasIndex(provider => provider.Enabled);
        providers.HasIndex(provider => provider.SortOrder);

        var infos = modelBuilder.Entity<ModelInfo>();
        infos.ToTable("ModelInfos");
        infos.HasKey(info => info.Id);
        infos.Property(info => info.Id).ValueGeneratedOnAdd();
        infos.Property(info => info.Scope).IsRequired();
        infos.Property(info => info.ModelKey).IsRequired();
        infos.Property(info => info.DisplayName).IsRequired();
        infos.Property(info => info.Description).IsRequired();
        infos.Property(info => info.MatchType).IsRequired();
        infos.Property(info => info.MatchPattern).IsRequired();
        infos.Property(info => info.CatalogJson).IsRequired();
        infos.Property(info => info.CapabilitiesJson).IsRequired();
        infos.Property(info => info.Source).IsRequired();
        infos.HasIndex(info => new { info.Scope, info.ProviderId, info.ModelKey });
        infos.HasIndex(info => new { info.Scope, info.ChannelId, info.ModelKey });
        infos.HasIndex(info => info.ProviderId);
        infos.HasIndex(info => info.ChannelId);
        infos.HasIndex(info => info.Enabled);
        infos.HasIndex(info => info.MatchPattern);
        infos.HasIndex(info => info.MatchType);

        var channelInfos = modelBuilder.Entity<ChannelModelInfo>();
        channelInfos.ToTable("ChannelModelInfos");
        channelInfos.HasKey(info => info.Id);
        channelInfos.Property(info => info.Id).ValueGeneratedOnAdd();
        channelInfos.Property(info => info.UpstreamModel).IsRequired();
        channelInfos.Property(info => info.ModelKey).IsRequired();
        channelInfos.Property(info => info.DisplayName).IsRequired();
        channelInfos.Property(info => info.Description).IsRequired();
        channelInfos.Property(info => info.MatchType).IsRequired();
        channelInfos.Property(info => info.MatchPattern).IsRequired();
        channelInfos.Property(info => info.CatalogJson).IsRequired();
        channelInfos.Property(info => info.CapabilitiesJson).IsRequired();
        channelInfos.Property(info => info.Source).IsRequired();
        channelInfos.HasIndex(info => new { info.ChannelId, info.UpstreamModel }).IsUnique();
        channelInfos.HasIndex(info => info.ProviderId);
        channelInfos.HasIndex(info => info.Enabled);
        channelInfos.HasIndex(info => info.MatchPattern);
        channelInfos.HasIndex(info => info.MatchType);

        var plans = modelBuilder.Entity<ModelPricingPlan>();
        plans.ToTable("ModelPricingPlans");
        plans.HasKey(plan => plan.Id);
        plans.Property(plan => plan.Id).ValueGeneratedOnAdd();
        plans.Property(plan => plan.Currency).IsRequired();
        plans.Property(plan => plan.TimeZoneId).IsRequired();
        plans.Property(plan => plan.OffPeakWindowsJson).IsRequired();
        plans.Property(plan => plan.Source).IsRequired();
        plans.HasIndex(plan => plan.ModelInfoId);
        plans.HasIndex(plan => plan.ChannelModelInfoId);
        plans.HasIndex(plan => plan.ChannelId);
        plans.HasIndex(plan => plan.Enabled);

        var rules = modelBuilder.Entity<ModelPricingRule>();
        rules.ToTable("ModelPricingRules");
        rules.HasKey(rule => rule.Id);
        rules.Property(rule => rule.Id).ValueGeneratedOnAdd();
        rules.Property(rule => rule.BillingItem).IsRequired();
        rules.Property(rule => rule.BillingMode).IsRequired();
        rules.Property(rule => rule.UnitPrice).HasPrecision(18, 8);
        rules.Property(rule => rule.TiersJson).IsRequired();
        rules.Property(rule => rule.OffPeakUnitPrice).HasPrecision(18, 8);
        rules.Property(rule => rule.OffPeakTiersJson).IsRequired();
        rules.HasIndex(rule => rule.PricingPlanId);
        rules.HasIndex(rule => rule.BillingItem);
        rules.HasIndex(rule => rule.Enabled);

        var mappings = modelBuilder.Entity<ChannelModelMapping>();
        mappings.ToTable("ChannelModelMappings");
        mappings.HasKey(mapping => mapping.Id);
        mappings.Property(mapping => mapping.Id).ValueGeneratedOnAdd();
        mappings.Property(mapping => mapping.RequestModel).IsRequired();
       mappings.Property(mapping => mapping.UpstreamModel).IsRequired();
       mappings.HasIndex(mapping => new { mapping.ChannelId, mapping.Position });
       mappings.HasIndex(mapping => new { mapping.ChannelId, mapping.RequestModel });
       mappings.HasIndex(mapping => mapping.Enabled);
    }

    private static void ConfigureRequestLogs(ModelBuilder modelBuilder)
    {
        var logs = modelBuilder.Entity<RequestLog>();
        logs.ToTable("RequestLogs");
        logs.HasKey(log => log.Id);
        logs.Property(log => log.Id).ValueGeneratedOnAdd();
        logs.HasIndex(log => log.CreatedAt);
        logs.HasIndex(log => log.Model);
        logs.HasIndex(log => log.UpstreamModel);
        logs.HasIndex(log => log.ChannelId);
        logs.HasIndex(log => log.PricingModelInfoId);
        logs.HasIndex(log => log.PricingPlanId);
        logs.HasIndex(log => log.RequestType);
        logs.HasIndex(log => log.LifecycleStatus);
        logs.HasIndex(log => log.ParentRequestLogId);
        logs.HasIndex(log => log.ConversationKey);
        logs.HasIndex(log => log.ConversationTurnId);
        logs.HasIndex(log => log.ConversationWindowId);
        logs.HasIndex(log => log.PreviousResponseId);
        logs.HasIndex(log => log.Path);
        logs.HasIndex(log => log.StatusCode);
        logs.HasIndex(log => log.ApiKeyId);
        logs.HasIndex(log => new { log.OwnerUserId, log.Id });

        var blocks = modelBuilder.Entity<LogContentBlock>();
        blocks.ToTable("LogContentBlocks");
        blocks.HasKey(block => block.Id);
        blocks.Property(block => block.Id).ValueGeneratedOnAdd();
        blocks.Property(block => block.Sha256).IsRequired();
        blocks.Property(block => block.RawLength).IsRequired();
        blocks.Property(block => block.StoredLength).IsRequired();
        blocks.Property(block => block.Compression).IsRequired();
        blocks.Property(block => block.Data).IsRequired();
        blocks.HasIndex(block => block.Sha256).IsUnique();

        var manifests = modelBuilder.Entity<LogContentManifest>();
        manifests.ToTable("LogContentManifests");
        manifests.HasKey(manifest => manifest.Id);
        manifests.Property(manifest => manifest.Id).ValueGeneratedOnAdd();
        manifests.Property(manifest => manifest.Sha256).IsRequired();
        manifests.Property(manifest => manifest.RawLength).IsRequired();
        manifests.Property(manifest => manifest.ChunkCount).IsRequired();
        manifests.Property(manifest => manifest.Encoding).IsRequired();
        manifests.HasIndex(manifest => manifest.Sha256).IsUnique();

        var manifestChunks = modelBuilder.Entity<LogContentManifestChunk>();
        manifestChunks.ToTable("LogContentManifestChunks");
        manifestChunks.HasKey(chunk => chunk.Id);
        manifestChunks.Property(chunk => chunk.Id).ValueGeneratedOnAdd();
        manifestChunks.Property(chunk => chunk.ManifestId).IsRequired();
        manifestChunks.Property(chunk => chunk.BlockId).IsRequired();
        manifestChunks.Property(chunk => chunk.RawLength).IsRequired();
        manifestChunks.HasIndex(chunk => new { chunk.ManifestId, chunk.Ordinal }).IsUnique();
        manifestChunks.HasIndex(chunk => chunk.BlockId);
        manifestChunks.HasOne<LogContentManifest>()
            .WithMany()
            .HasForeignKey(chunk => chunk.ManifestId)
            .OnDelete(DeleteBehavior.Cascade);
        manifestChunks.HasOne<LogContentBlock>()
            .WithMany()
            .HasForeignKey(chunk => chunk.BlockId)
            .OnDelete(DeleteBehavior.Restrict);

        var contentRefs = modelBuilder.Entity<RequestLogContentRef>();
        contentRefs.ToTable("RequestLogContentRefs");
        contentRefs.HasKey(reference => reference.Id);
        contentRefs.Property(reference => reference.Id).ValueGeneratedOnAdd();
        contentRefs.Property(reference => reference.RequestLogId).IsRequired();
        contentRefs.Property(reference => reference.Slot).IsRequired();
        contentRefs.Property(reference => reference.ManifestId).IsRequired();
        contentRefs.HasIndex(reference => new { reference.RequestLogId, reference.Slot }).IsUnique();
        contentRefs.HasIndex(reference => reference.ManifestId);
        contentRefs.HasOne<RequestLog>()
            .WithMany()
            .HasForeignKey(reference => reference.RequestLogId)
            .OnDelete(DeleteBehavior.Cascade);
        contentRefs.HasOne<LogContentManifest>()
            .WithMany()
            .HasForeignKey(reference => reference.ManifestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
