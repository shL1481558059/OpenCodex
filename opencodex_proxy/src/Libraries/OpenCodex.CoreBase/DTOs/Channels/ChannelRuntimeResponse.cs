using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.Channels;

/// <summary>
/// 表示单个渠道的运行时状态快照。配置态与运行时态分离到不同端点，此响应不含任何配置字段。
/// </summary>
public sealed class ChannelRuntimeResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("active_requests")]
    public int ActiveRequests { get; init; }

    [JsonPropertyName("health_status")]
    public string HealthStatus { get; init; } = "healthy";

    [JsonPropertyName("capacity")]
    public int? Capacity { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

/// <summary>
/// 表示渠道运行时状态列表响应。
/// </summary>
public sealed class ChannelRuntimeListResponse
{
    [JsonPropertyName("channels")]
    public IReadOnlyList<ChannelRuntimeResponse> Channels { get; init; } = [];
}
