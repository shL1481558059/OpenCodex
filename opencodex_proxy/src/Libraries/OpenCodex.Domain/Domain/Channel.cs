namespace OpenCodex.Core.Domain;

public sealed class Channel : BaseEntity<Guid>
{
    public Guid OwnerUserId { get; set; }

    public int Position { get; set; }

    public int Priority { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string AuthMode { get; set; } = "config";

    public string HeadersJson { get; set; } = "{}";

    public int TimeoutSeconds { get; set; }

    public int CircuitBreakDurationSeconds { get; set; }

    public int RetryCount { get; set; }

    public int Capacity { get; set; }

    public string CompatJson { get; set; } = "{}";

    public string ModelsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}
