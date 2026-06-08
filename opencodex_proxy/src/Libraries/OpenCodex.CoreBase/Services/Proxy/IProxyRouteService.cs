using OpenCodex.CoreBase.Routing;

namespace OpenCodex.CoreBase.Services.Proxy;

public interface IProxyRouteService
{
    RouteResult ChooseRoute(string ownerUsername, string? model);
}
