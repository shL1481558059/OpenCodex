namespace OpenCodex.Core.Domain;

/// <summary>
/// 表示某个 owner 的图片识别转移模型配置。一个 owner 最多一行,主路由必填,
/// 兜底两列同时为空或同时非空,该不变式由服务层维护。
/// </summary>
public sealed class VisionTransferSettings : BaseEntity<Guid>
{
    public Guid OwnerUserId { get; set; }

    public Guid PrimaryChannelId { get; set; }

    public string PrimaryModel { get; set; } = string.Empty;

    public Guid? FallbackChannelId { get; set; }

    public string? FallbackModel { get; set; }

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}
