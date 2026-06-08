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

    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    public DbSet<RequestLogDetail> RequestLogDetails => Set<RequestLogDetail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUsers(modelBuilder);
        ConfigureChannels(modelBuilder);
        ConfigureAccessApiKeys(modelBuilder);
        ConfigureWebSearch(modelBuilder);
        ConfigureRequestLogs(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<User>();
        entity.ToTable("Users");
        entity.HasKey(user => user.Username);
        entity.Property(user => user.Username).ValueGeneratedNever();
        entity.Property(user => user.PasswordHash).IsRequired();
        entity.Property(user => user.Role).IsRequired();
        entity.Ignore(user => user.Id);
        entity.Ignore(user => user.Channels);
    }

    private static void ConfigureChannels(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Channel>();
        entity.ToTable("Channels");
        entity.HasKey(channel => new { channel.OwnerUsername, channel.Id });
        entity.Property(channel => channel.OwnerUsername).ValueGeneratedNever();
        entity.Property(channel => channel.Id).ValueGeneratedNever();
        entity.Property(channel => channel.Name).IsRequired();
        entity.Property(channel => channel.Type).IsRequired();
        entity.Property(channel => channel.BaseUrl).IsRequired();
        entity.Property(channel => channel.ApiKey).IsRequired();
        entity.Property(channel => channel.AuthMode).IsRequired();
        entity.Property(channel => channel.HeadersJson).IsRequired();
        entity.Property(channel => channel.CompatJson).IsRequired();
        entity.Property(channel => channel.ModelsJson).IsRequired();
        entity.HasIndex(channel => new { channel.OwnerUsername, channel.Position });
        entity.Ignore(channel => channel.Owner);
    }

    private static void ConfigureAccessApiKeys(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AccessApiKey>();
        entity.ToTable("AccessApiKeys");
        entity.HasKey(key => key.Id);
        entity.Property(key => key.Name).IsRequired();
        entity.Property(key => key.OwnerUsername).IsRequired();
        entity.Property(key => key.KeyHash).IsRequired();
        entity.Property(key => key.KeyPrefix).IsRequired();
        entity.Property(key => key.KeySuffix).IsRequired();
        entity.HasIndex(key => key.KeyHash).IsUnique();
        entity.HasIndex(key => new { key.OwnerUsername, key.Id });
        entity
            .HasOne(key => key.Owner)
            .WithMany(user => user.AccessApiKeys)
            .HasForeignKey(key => key.OwnerUsername)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureWebSearch(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<WebSearchSettings>();
        settings.ToTable("WebSearchSettings");
        settings.HasKey(item => item.Id);
        settings.Property(item => item.Id).ValueGeneratedNever();

        var keys = modelBuilder.Entity<TavilyKey>();
        keys.ToTable("TavilyKeys");
        keys.HasKey(key => key.Id);
        keys.Property(key => key.Provider).IsRequired();
        keys.Property(key => key.ApiKey).IsRequired();
        keys.HasIndex(key => key.Position);
    }

    private static void ConfigureRequestLogs(ModelBuilder modelBuilder)
    {
        var logs = modelBuilder.Entity<RequestLog>();
        logs.ToTable("RequestLogs");
        logs.HasKey(log => log.Id);
        logs.HasIndex(log => log.CreatedAt);
        logs.HasIndex(log => log.Model);
        logs.HasIndex(log => log.UpstreamModel);
        logs.HasIndex(log => log.ChannelId);
        logs.HasIndex(log => log.Path);
        logs.HasIndex(log => log.StatusCode);
        logs.HasIndex(log => log.ApiKeyId);
        logs.HasIndex(log => new { log.OwnerUsername, log.Id });

        var details = modelBuilder.Entity<RequestLogDetail>();
        details.ToTable("RequestLogDetails");
        details.HasKey(detail => detail.RequestLogId);
        details
            .HasOne(detail => detail.RequestLog)
            .WithOne(log => log.Detail)
            .HasForeignKey<RequestLogDetail>(detail => detail.RequestLogId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
