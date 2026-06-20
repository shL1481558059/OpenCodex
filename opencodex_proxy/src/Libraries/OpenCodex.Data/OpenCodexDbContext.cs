using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;

namespace OpenCodex.Data;

public sealed class OpenCodexDbContext : DbContext
{
    public OpenCodexDbContext(DbContextOptions<OpenCodexDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<AccessApiKey> AccessApiKeys => Set<AccessApiKey>();

    public DbSet<WebSearchSettings> WebSearchSettings => Set<WebSearchSettings>();

    public DbSet<TavilyKey> TavilyKeys => Set<TavilyKey>();

    public DbSet<ModelPricing> ModelPricings => Set<ModelPricing>();

    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    public DbSet<RequestLogDetail> RequestLogDetails => Set<RequestLogDetail>();

    public DbSet<RequestLogStreamLine> RequestLogStreamLines => Set<RequestLogStreamLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUsers(modelBuilder);
        ConfigureChannels(modelBuilder);
        ConfigureAccessApiKeys(modelBuilder);
        ConfigureWebSearch(modelBuilder);
        ConfigureModelPricings(modelBuilder);
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
        entity.Property(channel => channel.Type).IsRequired();
        entity.Property(channel => channel.BaseUrl).IsRequired();
        entity.Property(channel => channel.ApiKey).IsRequired();
        entity.Property(channel => channel.AuthMode).IsRequired();
        entity.Property(channel => channel.HeadersJson).IsRequired();
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

        var keys = modelBuilder.Entity<TavilyKey>();
        keys.ToTable("TavilyKeys");
        keys.HasKey(key => key.Id);
        keys.Property(key => key.Id).ValueGeneratedOnAdd();
        keys.Property(key => key.Provider).IsRequired();
        keys.Property(key => key.ApiKey).IsRequired();
        keys.HasIndex(key => key.Position);
    }

    private static void ConfigureModelPricings(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ModelPricing>();
        entity.ToTable("ModelPricings");
        entity.HasKey(pricing => pricing.Id);
        entity.Property(pricing => pricing.Id).ValueGeneratedOnAdd();
        entity.Property(pricing => pricing.ModelId).IsRequired();
        entity.Property(pricing => pricing.Vendor).IsRequired();
        entity.Property(pricing => pricing.Name).IsRequired();
        entity.Property(pricing => pricing.MatchPattern).IsRequired();
        entity.Property(pricing => pricing.Source).IsRequired();
        entity.HasIndex(pricing => pricing.ModelId).IsUnique();
        entity.HasIndex(pricing => pricing.Vendor);
        entity.HasIndex(pricing => pricing.Enabled);
        entity.HasIndex(pricing => pricing.MatchPattern);
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
        logs.HasIndex(log => log.RequestType);
        logs.HasIndex(log => log.LifecycleStatus);
        logs.HasIndex(log => log.ParentRequestLogId);
        logs.HasIndex(log => log.Path);
        logs.HasIndex(log => log.StatusCode);
        logs.HasIndex(log => log.ApiKeyId);
        logs.HasIndex(log => new { log.OwnerUserId, log.Id });

        var details = modelBuilder.Entity<RequestLogDetail>();
        details.ToTable("RequestLogDetails");
        details.HasKey(detail => detail.RequestLogId);
        details.Property(detail => detail.RequestLogId).ValueGeneratedNever();

        var lines = modelBuilder.Entity<RequestLogStreamLine>();
        lines.ToTable("RequestLogStreamLines");
        lines.HasKey(line => line.Id);
        lines.Property(line => line.Id).ValueGeneratedOnAdd();
        lines.Property(line => line.Source).IsRequired();
        lines.Property(line => line.RawLine).IsRequired();
        lines.HasIndex(line => new { line.RequestLogId, line.Sequence }).IsUnique();
        lines.HasIndex(line => line.OccurredAt);
    }
}
