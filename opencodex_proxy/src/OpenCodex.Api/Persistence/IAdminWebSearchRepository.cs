using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IAdminWebSearchRepository
{
    WebSearchConfigRecord ReadWebSearchConfig();

    WebSearchConfigRecord ReplaceWebSearchConfig(
        IReadOnlyDictionary<string, object?> config);

    TavilyKeyRecord? ReserveTavilyKeyById(long keyId);
}
