namespace OpenCodex.CoreBase.DTOs;

public sealed class ChannelDto(
    string ownerUsername,
    string id,
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
    bool enabled)
{
    public string OwnerUsername { get; } = ownerUsername;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Type { get; } = type;

    public string BaseUrl { get; } = baseUrl;

    public string ApiKey { get; } = apiKey;

    public string AuthMode { get; } = authMode;

    public IReadOnlyDictionary<string, object?> Headers { get; } = headers;

    public int TimeoutSeconds { get; } = timeoutSeconds;

    public int RetryCount { get; } = retryCount;

    public IReadOnlyDictionary<string, object?> Compat { get; } = compat;

    public IReadOnlyList<object?> Models { get; } = models;

    public bool Enabled { get; } = enabled;
}

public sealed class UserDto(
    string username,
    string role,
    bool enabled,
    double createdAt,
    double updatedAt)
{
    public string Username { get; } = username;

    public string Role { get; } = role;

    public bool Enabled { get; } = enabled;

    public double CreatedAt { get; } = createdAt;

    public double UpdatedAt { get; } = updatedAt;
}
