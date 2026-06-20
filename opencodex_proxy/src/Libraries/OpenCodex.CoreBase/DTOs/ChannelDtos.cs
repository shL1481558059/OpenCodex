namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示管理接口返回的上游通道配置。
/// </summary>
public sealed class ChannelDto(
    Guid id,
    Guid ownerUserId,
    string ownerUsername,
    int position,
    string name,
    string type,
    string baseUrl,
    string apiKey,
    string authMode,
    IReadOnlyDictionary<string, object?> headers,
    int timeoutSeconds,
    int retryCount,
    int priority,
    int? capacity,
    IReadOnlyDictionary<string, object?> compat,
    IReadOnlyList<object?> models,
    bool enabled)
{
    public Guid Id { get; } = id;

    public Guid OwnerUserId { get; } = ownerUserId;

    public string OwnerUsername { get; } = ownerUsername;

    public int Position { get; } = position;

    public string Name { get; } = name;

    public string Type { get; } = type;

    public string BaseUrl { get; } = baseUrl;

    public string ApiKey { get; } = apiKey;

    public string AuthMode { get; } = authMode;

    public IReadOnlyDictionary<string, object?> Headers { get; } = headers;

    public int TimeoutSeconds { get; } = timeoutSeconds;

    public int RetryCount { get; } = retryCount;

    public int Priority { get; } = priority;

    public int? Capacity { get; } = capacity;

    public IReadOnlyDictionary<string, object?> Compat { get; } = compat;

    public IReadOnlyList<object?> Models { get; } = models;

    public bool Enabled { get; } = enabled;
}

/// <summary>
/// 表示管理接口返回的管理员用户。
/// </summary>
public sealed class UserDto(
    Guid id,
    string username,
    string role,
    bool enabled,
    double createdAt,
    double updatedAt)
{
    public Guid Id { get; } = id;

    public string Username { get; } = username;

    public string Role { get; } = role;

    public bool Enabled { get; } = enabled;

    public double CreatedAt { get; } = createdAt;

    public double UpdatedAt { get; } = updatedAt;
}
