using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IProxyWebSearchRepository
{
    WebSearchConfigRecord ReadWebSearchConfig();

    TavilyKeyRecord? ReserveTavilyKey();
}
