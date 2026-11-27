using OpenCodex.Core.Errors;
using OpenCodex.ProtocolConversion;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

internal static class ModelSimulationPolicy
{
    public static async Task<bool> IsEnabledAsync(
        IModelCatalogService catalog,
        IProxyRouteService routes,
        string ownerUsername,
        string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        try
        {
            var candidates = await routes.ListRouteCandidatesAsync(ownerUsername, model);
            if (candidates.Count > 0)
            {
                var route = candidates[0];
                if (MultiAgentV2Policy.IsNativeResponsesPassthrough(
                    ProtocolConverter.Responses, JsonDictionaryValue.String(route.Channel, "type")))
                {
                    return false;
                }

                var channelId = Guid.TryParse(JsonDictionaryValue.String(route.Channel, "id"), out var parsed)
                    ? parsed
                    : (Guid?)null;
                return catalog.SimulatesMultiAgent(channelId, model, route.UpstreamModel);
            }
        }
        catch (RoutingException)
        {
            // Keep route errors in the existing proxy/runtime error handling path.
        }

        return catalog.SimulatesMultiAgent(model);
    }
}
