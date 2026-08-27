using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.OpenApi;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Caching;
using OpenCodex.Core.Services.Events;
using OpenCodex.CoreBase.Events;
using OpenCodex.CoreBase.Caching;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using OpenCodex.CoreBase.Data;
using OpenCodex.Data;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexServiceCollectionExtensions
{
    public static IServiceCollection AddOpenCodexApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "OpenCodex Proxy API",
                Version = "v1",
                Description = "Admin, observability, and OpenAI-compatible proxy endpoints."
            });
        });
        services.AddOpenCodexServices();
        services.AddOpenCodexAuthentication(configuration);

        return services;
    }

    private static IServiceCollection AddOpenCodexServices(this IServiceCollection services)
    {
        services.AddDbContext<OpenCodexSqliteDbContext>((serviceProvider, options) =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
                .GetSettings();
            OpenCodexDbContextFactory.ConfigureSqlite(
                options,
                settings.ConnectionString);
        });
        services.AddDbContext<OpenCodexPostgresDbContext>((serviceProvider, options) =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
                .GetSettings();
            OpenCodexDbContextFactory.ConfigurePostgres(
                options,
                settings.ConnectionString);
        });
        services.AddScoped<IOpenCodexDbContext>(serviceProvider =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
                .GetSettings();
            return OpenCodexDbContextFactory.NormalizeProvider(settings.DatabaseProvider) switch
            {
                "sqlite" => serviceProvider.GetRequiredService<OpenCodexSqliteDbContext>(),
                "postgres" => serviceProvider.GetRequiredService<OpenCodexPostgresDbContext>(),
                _ => throw new InvalidOperationException(
                    $"Unsupported database provider: '{settings.DatabaseProvider}'. Supported values: sqlite, postgres.")
            };
        });
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));

        // 缓存基础设施:L1 进程内内存 + L2 Redis(可选降级)。
        services.AddMemoryCache();
        services.AddSingleton<IRedisConnectionProvider>(serviceProvider =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
                .GetSettings();
            return new RedisConnectionProvider(settings.RedisConnection, settings.RedisPrefix);
        });
        services.AddSingleton<ICacheService>(serviceProvider =>
        {
            var settings = serviceProvider
                .GetRequiredService<IOpenCodexRuntimeSettingsProvider>()
                .GetSettings();
            return new TwoLevelCacheService(
                serviceProvider.GetRequiredService<IMemoryCache>(),
                serviceProvider.GetRequiredService<IRedisConnectionProvider>(),
                TimeSpan.FromSeconds(settings.CacheDefaultTtlSeconds));
        });

        services.AddHttpClient<IUpstreamClient, HttpUpstreamClient>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 100,
                EnableMultipleHttp2Connections = true
            });
        
        services.AddHttpClient<IUpstreamModelClient, HttpUpstreamClient>()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 100,
                EnableMultipleHttp2Connections = true
            });
        
        services.AddHttpClient<IWebSearchClient, TavilyWebSearchClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 50
            });

        // Model catalog sync: independent HttpClient with 60s timeout,
        // automatic decompression, max 3 redirects, 5MB response cap.
        services.AddHttpClient<IModelCatalogSyncClient, ModelCatalogSyncClient>()
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 10,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
        services.AddSingleton<IOpenCodexRuntimeSettingsProvider, OpenCodexRuntimeSettingsProvider>();
        services.AddSingleton<IDesktopSystemSettingsStore, DesktopSystemSettingsStore>();
        services.AddScoped<IRequestBodyReader, RequestBodyReader>();
        services.AddScoped<IImageEditRequestReader, ImageEditRequestReader>();
        services.AddScoped<IWorkContext, WebWorkContext>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IChannelDiagnosticsService, ChannelDiagnosticsService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IConfigService, ConfigService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<IModelCatalogService, ModelCatalogService>();
        services.AddScoped<IModelCatalogSyncService, ModelCatalogSyncService>();
        services.AddScoped<IObservabilityService, ObservabilityService>();
        services.AddScoped<IWebSearchService, WebSearchService>();
        services.AddScoped<IProxyAccessService, ProxyAccessService>();
        services.AddScoped<IProxyEndpointService, ProxyEndpointService>();
        services.AddScoped<IProxyImageFallbackService, ProxyImageFallbackService>();
        services.AddScoped<IProxyImagePayloadRewriter, ProxyImagePayloadRewriter>();
        services.AddScoped<IProxyLogService, ProxyLogService>();
        services.AddSingleton<IChannelCapacityService, ChannelCapacityService>();
        services.AddSingleton<IChannelCircuitBreakerService, ChannelCircuitBreakerService>();
        services.AddSingleton<IChannelAffinityService, ChannelAffinityService>();
        services.AddScoped<IProxyOcrService, ProxyOcrService>();
        services.AddScoped<IProxyRequestService, ProxyRequestService>();
        services.AddScoped<IProxyRouteService, ProxyRouteService>();
        services.AddScoped<IProxyNonStreamService, ProxyNonStreamService>();
        services.AddScoped<IProxyStreamService, ProxyStreamService>();
        services.AddScoped<IWebSearchSimulator, WebSearchSimulator>();
        services.AddSingleton<ICodexOfficialModelCatalogFactory, CodexOfficialModelCatalogFactory>();
        services.AddSingleton<IEventBus>(serviceProvider =>
        {
            var redis = serviceProvider.GetService<IRedisConnectionProvider>();
            return new EventBus(redis);
        });

        return services;
    }

    private static IServiceCollection AddOpenCodexAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(ResolveDataProtectionKeysPath(configuration)))
            .SetApplicationName(BuildDataProtectionApplicationName(configuration));
        services.AddAuthentication(IAuthService.AuthenticationScheme)
            .AddCookie(IAuthService.AuthenticationScheme, options =>
            {
                options.Cookie.Name = IAuthService.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.IsEssential = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(ReadAdminCookieDays(configuration));
                options.SlidingExpiration = true;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();

        return services;
    }

    private static int ReadAdminCookieDays(IConfiguration configuration)
    {
        var rawValue = ConfigValue(configuration, "OpenCodex:AdminCookieDays", "OPENCODEX_ADMIN_COOKIE_DAYS");
        return int.TryParse(rawValue, out var days) && days > 0
            ? days
            : 30;
    }

    private static string ResolveDataProtectionKeysPath(IConfiguration configuration)
    {
        var configured = ConfigValue(
            configuration,
            "OpenCodex:DataProtectionKeysPath",
            "OPENCODEX_DATA_PROTECTION_KEYS_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var absoluteConfigured = Path.GetFullPath(configured.Trim());
            Directory.CreateDirectory(absoluteConfigured);
            return absoluteConfigured;
        }

        // 显式配置优先;否则回退到运行目录下的 logs/.keys。
        // 注:此前版本依赖 DbPath 目录推断,切换到多 provider 后该路径不再可用,改为固定默认。
        var keysPath = Path.GetFullPath("logs/.keys");
        Directory.CreateDirectory(keysPath);
        return keysPath;
    }

    private static string BuildDataProtectionApplicationName(IConfiguration configuration)
    {
        var secret = (ConfigValue(configuration, "OpenCodex:SecretKey", "OPENCODEX_SECRET_KEY") ?? "change-me-session-secret").Trim();
        if (secret.Length == 0)
        {
            secret = "change-me-session-secret";
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        return $"OpenCodex.Admin.{digest[..16]}";
    }

    private static string? ConfigValue(
        IConfiguration configuration,
        string primaryKey,
        string fallbackKey)
    {
        return configuration[primaryKey] ?? configuration[fallbackKey];
    }
}
