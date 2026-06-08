using Microsoft.OpenApi;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Services.Admin;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services.Admin;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

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
        services.AddScoped<IOpenCodexRuntimeSettingsProvider, OpenCodexRuntimeSettingsProvider>();
        services.AddScoped<IRequestBodyReader, RequestBodyReader>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
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
