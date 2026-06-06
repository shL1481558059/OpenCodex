using OpenCodex.Api.Domain;

namespace OpenCodex.Api.Services;

public sealed class AdminConfigImportResult
{
    public AdminConfigImportResult(
        IReadOnlyList<ChannelRecord> config,
        int imported,
        int skipped,
        IReadOnlyList<string> skippedIds)
    {
        Config = config;
        Imported = imported;
        Skipped = skipped;
        SkippedIds = skippedIds;
    }

    public IReadOnlyList<ChannelRecord> Config { get; }

    public int Imported { get; }

    public int Skipped { get; }

    public IReadOnlyList<string> SkippedIds { get; }
}
