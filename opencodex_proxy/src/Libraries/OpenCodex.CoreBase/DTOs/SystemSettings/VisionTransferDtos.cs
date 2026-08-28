using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs.SystemSettings;

public sealed class VisionTransferCandidateDto
{
    [JsonPropertyName("channel_id")]
    public Guid ChannelId { get; set; }

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("channel_type")]
    public string ChannelType { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; set; } = string.Empty;
}

public sealed class VisionTransferCandidateListResponse
{
    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; set; } = string.Empty;

    [JsonPropertyName("candidates")]
    public IReadOnlyList<VisionTransferCandidateDto> Candidates { get; set; } = [];
}

public sealed class VisionTransferConfigItemDto
{
    [JsonPropertyName("channel_id")]
    public Guid? ChannelId { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public sealed class VisionTransferSettingsUpdateRequest
{
    [JsonPropertyName("owner_username")]
    public string? OwnerUsername { get; set; }

    [JsonPropertyName("primary")]
    public required VisionTransferConfigItemDto Primary { get; set; }

    [JsonPropertyName("fallback")]
    public VisionTransferConfigItemDto? Fallback { get; set; }
}

public sealed class VisionTransferConfigStatusDto
{
    [JsonPropertyName("channel_id")]
    public Guid ChannelId { get; set; }

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("channel_type")]
    public string ChannelType { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("upstream_model")]
    public string UpstreamModel { get; set; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class VisionTransferSettingsResponse
{
    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; set; } = string.Empty;

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("primary")]
    public VisionTransferConfigStatusDto? Primary { get; set; }

    [JsonPropertyName("fallback")]
    public VisionTransferConfigStatusDto? Fallback { get; set; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; set; }
}

public sealed class VisionTransferSettingsSnapshot
{
    public Guid PrimaryChannelId { get; }
    public string PrimaryModel { get; }
    public Guid? FallbackChannelId { get; }
    public string? FallbackModel { get; }

    public VisionTransferSettingsSnapshot(
        Guid primaryChannelId,
        string primaryModel,
        Guid? fallbackChannelId,
        string? fallbackModel)
    {
        PrimaryChannelId = primaryChannelId;
        PrimaryModel = primaryModel;
        FallbackChannelId = fallbackChannelId;
        FallbackModel = fallbackModel;
    }
}
