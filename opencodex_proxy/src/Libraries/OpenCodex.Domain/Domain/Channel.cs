namespace OpenCodex.Core.Domain;

public sealed class Channel : BaseEntity
{
    public string OwnerUsername { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public int Position { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string AuthMode { get; set; } = "config";

    public string HeadersJson { get; set; } = "{}";

    public int TimeoutSeconds { get; set; }

    public int RetryCount { get; set; }

    public string CompatJson { get; set; } = "{}";

    public string ModelsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }

    public User? Owner { get; set; }

    public override object? GetId()
    {
        return OwnerUsername.Length == 0 || Id.Length == 0 ? null : (OwnerUsername, Id);
    }
}
