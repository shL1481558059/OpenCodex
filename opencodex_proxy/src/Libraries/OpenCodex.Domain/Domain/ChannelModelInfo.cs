namespace OpenCodex.Core.Domain;

public sealed class ChannelModelInfo : BaseEntity<Guid>
{
    public Guid ChannelId { get; set; }

    public string UpstreamModel { get; set; } = string.Empty;

    public Guid ProviderId { get; set; }

    public string ModelKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string MatchType { get; set; } = ModelMatchTypes.Exact;

    public string MatchPattern { get; set; } = string.Empty;

    public string CatalogJson { get; set; } = "{}";

    public string CapabilitiesJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}
