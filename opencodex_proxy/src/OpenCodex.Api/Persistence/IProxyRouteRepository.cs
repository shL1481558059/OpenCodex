using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IProxyRouteRepository
{
    IReadOnlyList<ChannelRecord> ReadChannels(string ownerUsername);
}
