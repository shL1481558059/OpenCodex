using OpenCodex.Api.Config;
using OpenCodex.Api.Domain;
using OpenCodex.Api.Errors;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Routing;

namespace OpenCodex.Api.Services;

public sealed class ProxyRouteService : IProxyRouteService
{
    private readonly IProxyRouteRepository _routes;

    public ProxyRouteService(IProxyRouteRepository routes)
    {
        _routes = routes;
    }

    public RouteResult ChooseRoute(string ownerUsername, string? model)
    {
        var channels = _routes.ReadChannels(ownerUsername)
            .Select(ChannelToConfig)
            .ToList<object?>();
        var config = new Dictionary<string, object?>
        {
            ["channels"] = channels
        };
        var expanded = ConfigEnvironmentExpander.Expand(config);
        if (!ConfigValue.TryAsObject(expanded, out var expandedObject))
        {
            throw new BadRequestException("expanded config must be an object");
        }

        return ChannelRouter.ChooseChannel(expandedObject, model);
    }

    private static Dictionary<string, object?> ChannelToConfig(ChannelRecord channel)
    {
        return new Dictionary<string, object?>
        {
            ["owner_username"] = channel.OwnerUsername,
            ["id"] = channel.Id,
            ["name"] = channel.Name,
            ["type"] = channel.Type,
            ["baseurl"] = channel.BaseUrl,
            ["apikey"] = channel.ApiKey,
            ["auth_mode"] = channel.AuthMode,
            ["headers"] = channel.Headers,
            ["timeout_seconds"] = channel.TimeoutSeconds,
            ["retry_count"] = channel.RetryCount,
            ["compat"] = channel.Compat,
            ["models"] = channel.Models,
            ["enabled"] = channel.Enabled
        };
    }
}
