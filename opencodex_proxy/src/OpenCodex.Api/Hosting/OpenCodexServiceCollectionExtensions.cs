using Microsoft.OpenApi;
using OpenCodex.Api.Abstractions;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.ExternalIntegrations;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexServiceCollectionExtensions
{
    public static IServiceCollection AddOpenCodexApi(this IServiceCollection services)
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
        services.AddOpenCodexSession();

        return services;
    }

    private static IServiceCollection AddOpenCodexServices(this IServiceCollection services)
    {
        services.AddHttpClient<IUpstreamClient, HttpUpstreamClient>();
        services.AddHttpClient<IUpstreamModelClient, HttpUpstreamClient>();
        services.AddHttpClient<IWebSearchClient, TavilyWebSearchClient>();
        services.AddScoped<IAdminStaticDirectoryProvider, AdminStaticDirectoryProvider>();
        services.AddScoped<IOpenCodexRuntimeSettingsProvider, OpenCodexRuntimeSettingsProvider>();
        services.AddScoped(typeof(IRepository<>), typeof(SqliteRepository<>));
        services.AddScoped<IRequestBodyReader, RequestBodyReader>();
        services.AddScoped<IAdminConfigRepository, AdminConfigRepository>();
        services.AddScoped<IAdminUserRepository, AdminUserRepository>();
        services.AddScoped<IAdminApiKeyRepository, AdminApiKeyRepository>();
        services.AddScoped<IAdminObservabilityRepository, AdminObservabilityRepository>();
        services.AddScoped<IAdminWebSearchRepository, AdminWebSearchRepository>();
        services.AddScoped<IProxyAccessRepository, ProxyAccessRepository>();
        services.AddScoped<IProxyLogRepository, ProxyLogRepository>();
        services.AddScoped<IProxyRouteRepository, ProxyRouteRepository>();
        services.AddScoped<IProxyWebSearchRepository, ProxyWebSearchRepository>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
        services.AddScoped<IAdminUiService, AdminUiService>();
        services.AddScoped<IAdminChannelDiagnosticsService, AdminChannelDiagnosticsService>();
        services.AddScoped<IAdminSessionService, AdminSessionService>();
        services.AddScoped<IAdminConfigService, AdminConfigService>();
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<IAdminApiKeyService, AdminApiKeyService>();
        services.AddScoped<IAdminObservabilityService, AdminObservabilityService>();
        services.AddScoped<IAdminWebSearchService, AdminWebSearchService>();
        services.AddScoped<IProxyAccessService, ProxyAccessService>();
        services.AddScoped<IProxyEndpointService, ProxyEndpointService>();
        services.AddScoped<IProxyLogService, ProxyLogService>();
        services.AddScoped<IProxyRequestService, ProxyRequestService>();
        services.AddScoped<IProxyRouteService, ProxyRouteService>();
        services.AddScoped<IProxyNonStreamService, ProxyNonStreamService>();
        services.AddScoped<IProxyStreamService, ProxyStreamService>();
        services.AddScoped<IWebSearchSimulator, WebSearchSimulator>();

        return services;
    }

    private static IServiceCollection AddOpenCodexSession(this IServiceCollection services)
    {
        services.AddDistributedMemoryCache();
        services.AddSession(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });

        return services;
    }
}
