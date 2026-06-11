namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示管理接口返回的上游通道配置。
/// </summary>
/// <param name="ownerUsername">拥有该通道的用户名。</param>
/// <param name="id">通道标识符。</param>
/// <param name="position">渠道保存顺序位置。</param>
/// <param name="name">通道显示名称。</param>
/// <param name="type">上游提供方类型。</param>
/// <param name="baseUrl">上游基础 URL。</param>
/// <param name="apiKey">上游 API 密钥。</param>
/// <param name="authMode">上游请求使用的认证模式。</param>
/// <param name="headers">应用到上游请求的附加请求头。</param>
/// <param name="timeoutSeconds">上游请求超时时间，单位为秒。</param>
/// <param name="retryCount">重试次数。</param>
/// <param name="priority">渠道优先级；值越小优先级越高。</param>
/// <param name="capacity">渠道允许的主请求并发上限；为空表示不限。</param>
/// <param name="compat">通道兼容性选项。</param>
/// <param name="models">通道配置的模型。</param>
/// <param name="enabled">指示通道是否启用的值。</param>
public sealed class ChannelDto(
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
    int priority,
    int? capacity,
    IReadOnlyDictionary<string, object?> compat,
    IReadOnlyList<object?> models,
    bool enabled)
{
    /// <summary>
    /// 获取拥有该通道的用户名。
    /// </summary>
    public string OwnerUsername { get; } = ownerUsername;

    /// <summary>
    /// 获取通道标识符。
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// 获取通道显示名称。
    /// </summary>
    public int Position { get; } = position;

    /// <summary>
    /// 获取通道显示名称。
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// 获取上游提供方类型。
    /// </summary>
    public string Type { get; } = type;

    /// <summary>
    /// 获取上游基础 URL。
    /// </summary>
    public string BaseUrl { get; } = baseUrl;

    /// <summary>
    /// 获取上游 API 密钥。
    /// </summary>
    public string ApiKey { get; } = apiKey;

    /// <summary>
    /// 获取上游请求使用的认证模式。
    /// </summary>
    public string AuthMode { get; } = authMode;

    /// <summary>
    /// 获取应用到上游请求的附加请求头。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Headers { get; } = headers;

    /// <summary>
    /// 获取上游请求超时时间，单位为秒。
    /// </summary>
    public int TimeoutSeconds { get; } = timeoutSeconds;

    /// <summary>
    /// 获取重试次数。
    /// </summary>
    public int RetryCount { get; } = retryCount;

    /// <summary>
    /// 获取渠道优先级；值越小优先级越高。
    /// </summary>
    public int Priority { get; } = priority;

    /// <summary>
    /// 获取渠道允许的主请求并发上限；为空表示不限。
    /// </summary>
    public int? Capacity { get; } = capacity;

    /// <summary>
    /// 获取通道兼容性选项。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Compat { get; } = compat;

    /// <summary>
    /// 获取通道配置的模型。
    /// </summary>
    public IReadOnlyList<object?> Models { get; } = models;

    /// <summary>
    /// 获取指示通道是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;
}

/// <summary>
/// 表示管理接口返回的管理员用户。
/// </summary>
/// <param name="username">管理员用户名。</param>
/// <param name="role">管理员角色。</param>
/// <param name="enabled">指示用户是否启用的值。</param>
/// <param name="createdAt">创建时间戳。</param>
/// <param name="updatedAt">最后更新时间戳。</param>
public sealed class UserDto(
    string username,
    string role,
    bool enabled,
    double createdAt,
    double updatedAt)
{
    /// <summary>
    /// 获取管理员用户名。
    /// </summary>
    public string Username { get; } = username;

    /// <summary>
    /// 获取管理员角色。
    /// </summary>
    public string Role { get; } = role;

    /// <summary>
    /// 获取指示用户是否启用的值。
    /// </summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// 获取创建时间戳。
    /// </summary>
    public double CreatedAt { get; } = createdAt;

    /// <summary>
    /// 获取最后更新时间戳。
    /// </summary>
    public double UpdatedAt { get; } = updatedAt;
}
