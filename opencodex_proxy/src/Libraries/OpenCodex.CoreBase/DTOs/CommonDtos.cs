using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示下拉列表选项的通用数据传输对象。
/// </summary>
/// <typeparam name="TId">选项标识类型。</typeparam>
public sealed class SelectOption<TId>
{
    /// <summary>
    /// 初始化 <see cref="SelectOption{TId}"/> 类的新实例。
    /// </summary>
    /// <param name="id">选项标识。</param>
    /// <param name="name">选项显示名称。</param>
    public SelectOption(TId id, string? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>
    /// 获取选项标识。
    /// </summary>
    [JsonPropertyName("id")]
    public TId Id { get; }

    /// <summary>
    /// 获取选项显示名称。
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; }
}
