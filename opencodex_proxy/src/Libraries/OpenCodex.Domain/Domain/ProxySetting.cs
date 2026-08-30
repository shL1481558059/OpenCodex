namespace OpenCodex.Core.Domain;

/// <summary>
/// 代理功能开关的 key/value 存储项。后续扩展开关时只新增 key 常量,
/// 无需改动表结构。
/// </summary>
public sealed class ProxySetting : BaseEntity<Guid>
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public double CreatedAt { get; set; }

    public double UpdatedAt { get; set; }
}
