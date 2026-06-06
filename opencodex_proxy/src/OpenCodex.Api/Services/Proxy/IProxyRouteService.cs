using OpenCodex.Api.Routing;

namespace OpenCodex.Api.Services;

public interface IProxyRouteService
{
    RouteResult ChooseRoute(string ownerUsername, string? model);
}
