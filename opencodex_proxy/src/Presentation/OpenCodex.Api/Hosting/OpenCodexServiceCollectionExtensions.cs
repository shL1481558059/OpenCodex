using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Controllers;
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
    public static IServiceCollection AddOpenCodexApi(
        this IServiceCollection services,
        IConfiguration configuration)
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
        services.AddOpenCodexAuthentication(configuration);

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
        services.AddSingleton<IChannelCapacityService, ChannelCapacityService>();
        services.AddSingleton<IChannelAffinityService, ChannelAffinityService>();
        services.AddSingleton<ILocalImageOcrService, LocalPaddleImageOcrService>();
        services.AddScoped<IProxyOcrService, ProxyOcrService>();
        services.AddScoped<IProxyRequestService, ProxyRequestService>();
        services.AddScoped<IProxyRouteService, ProxyRouteService>();
        services.AddScoped<IProxyNonStreamService, ProxyNonStreamService>();
        services.AddScoped<IProxyStreamService, ProxyStreamService>();
        services.AddScoped<IWebSearchSimulator, WebSearchSimulator>();

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
        services.AddAuthentication(SessionState.AuthenticationScheme)
            .AddCookie(SessionState.AuthenticationScheme, options =>
            {
                options.Cookie.Name = SessionState.CookieName;
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

        var dbPath = ConfigValue(configuration, "OpenCodex:DbPath", "OPENCODEX_DB_PATH") ?? "logs/opencodex.db";
        var absoluteDbPath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(absoluteDbPath) ?? AppContext.BaseDirectory;
        var keysPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(absoluteDbPath)}.keys");
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
