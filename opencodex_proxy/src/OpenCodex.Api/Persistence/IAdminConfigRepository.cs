using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Persistence;

public interface IAdminConfigRepository
{
    IReadOnlyList<ChannelRecord> ReadChannels(string? ownerUsername);

    void ReplaceChannels(
        IEnumerable<IReadOnlyDictionary<string, object?>> channels,
        string? ownerUsername);
}
