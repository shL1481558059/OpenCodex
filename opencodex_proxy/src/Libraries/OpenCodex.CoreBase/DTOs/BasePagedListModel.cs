using System.Text.Json.Serialization;

namespace OpenCodex.CoreBase.DTOs;

/// <summary>
/// 表示分页列表响应的基类。所有列表端点统一继承此类，保证 items、total、page、page_size 四个字段。
/// </summary>
/// <typeparam name="T">列表元素类型。</typeparam>
public abstract class BasePagedListModel<T>
{
   [JsonPropertyName("items")]
   public IReadOnlyList<T> Items { get; init; } = [];

   [JsonPropertyName("total")]
   public int Total { get; init; }

   [JsonPropertyName("page")]
   public int Page { get; init; }

   [JsonPropertyName("page_size")]
   public int PageSize { get; init; }
}
