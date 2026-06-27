namespace OpenCodex.Core.Domain;

public sealed class ModelProvider : BaseEntity<Guid>
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    public string Source { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}
