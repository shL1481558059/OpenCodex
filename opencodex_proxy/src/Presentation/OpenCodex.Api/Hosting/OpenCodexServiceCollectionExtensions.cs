using Microsoft.OpenApi;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.Core.Services;
using OpenCodex.Core.Services.Mapping;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Api.Hosting;

public static class OpenCodexServiceCollectionExtensions
{
    public static IServiceCollection AddOpenCodexApi(this IServiceCollection services)
    {
        OpenCodexMappingConfig.Register();
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
        services.AddSingleton<IOpenCodexRuntimeSettingsProvider, OpenCodexRuntimeSettingsProvider>();
        services.AddScoped<IRequestBodyReader, RequestBodyReader>();
        services.AddScoped<IWorkContext, WebWorkContext>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IChannelDiagnosticsService, ChannelDiagnosticsService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IConfigService, ConfigService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<IModelPricingService, ModelPricingService>();
        services.AddScoped<IObservabilityService, ObservabilityService>();
        services.AddScoped<IWebSearchService, WebSearchService>();
        services.AddScoped<IProxyAccessService, ProxyAccessService>();
        services.AddScoped<IProxyEndpointService, ProxyEndpointService>();
        services.AddScoped<IProxyImageFallbackService, ProxyImageFallbackService>();
        services.AddScoped<IProxyImagePayloadRewriter, ProxyImagePayloadRewriter>();
        services.AddScoped<IProxyLogService, ProxyLogService>();
        services.AddSingleton<ILocalImageOcrService, LocalPaddleImageOcrService>();
        services.AddScoped<IProxyOcrService, ProxyOcrService>();
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
        services.AddHttpContextAccessor();
        services.AddSession(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });

        return services;
    }
}
