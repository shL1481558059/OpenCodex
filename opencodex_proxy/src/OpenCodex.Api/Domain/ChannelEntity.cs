namespace OpenCodex.Api.Domain;

public sealed class ChannelEntity : BaseEntity
{
    public ChannelEntity(
        string ownerUsername,
        string id,
        int position,
        string name,
        string type,
        string baseUrl,
        string apiKey,
        string authMode,
        IReadOnlyDictionary<string, object?> headers,
        int timeoutSeconds,
        int retryCount,
        IReadOnlyDictionary<string, object?> compat,
        IReadOnlyList<object?> models,
        bool enabled,
        double createdAt,
        double updatedAt)
    {
        OwnerUsername = ownerUsername;
        Id = id;
        Position = position;
        Name = name;
        Type = type;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        AuthMode = authMode;
        Headers = headers;
        TimeoutSeconds = timeoutSeconds;
        RetryCount = retryCount;
        Compat = compat;
        Models = models;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string OwnerUsername { get; init; }

    public string Id { get; init; }

    public int Position { get; init; }

    public string Name { get; init; }

    public string Type { get; init; }

    public string BaseUrl { get; init; }

    public string ApiKey { get; init; }

    public string AuthMode { get; init; }

    public IReadOnlyDictionary<string, object?> Headers { get; init; }

    public int TimeoutSeconds { get; init; }

    public int RetryCount { get; init; }

    public IReadOnlyDictionary<string, object?> Compat { get; init; }

    public IReadOnlyList<object?> Models { get; init; }

    public bool Enabled { get; init; }

    public double CreatedAt { get; init; }

    public double UpdatedAt { get; init; }

    public override object? GetId()
    {
        return OwnerUsername.Length == 0 || Id.Length == 0 ? null : (OwnerUsername, Id);
    }

    public ChannelRecord ToRecord()
    {
        return new ChannelRecord(
            OwnerUsername,
            Id,
            Name,
            Type,
            BaseUrl,
            ApiKey,
            AuthMode,
            Headers,
            TimeoutSeconds,
            RetryCount,
            Compat,
            Models,
            Enabled);
    }
}
