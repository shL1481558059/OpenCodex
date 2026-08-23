namespace OpenCodex.CoreBase.DTOs.Observability;

/// <summary>
/// 表示日志查询的筛选参数，收敛 GET /logs 和 GET /stats 的平铺查询参数。
/// </summary>
public class LogFilterQuery
{
    public string? RequestId { get; set; }
    public string? Model { get; set; }
    public string? UpstreamModel { get; set; }
    public string? ChannelId { get; set; }
    public string? OwnerUsername { get; set; }
    public string? ApiKeyId { get; set; }
    public string? Path { get; set; }
    public string? RequestType { get; set; }
    public string? StatusCode { get; set; }
    public string? IsStream { get; set; }
    public string? ClientIp { get; set; }
    public string? Error { get; set; }
    public string? RequestStatus { get; set; }
    public string? CreatedFrom { get; set; }
    public string? CreatedTo { get; set; }
    public string? ConversationKey { get; set; }
    public string? ConversationTurnId { get; set; }
    public string? ConversationWindowId { get; set; }
    public string? PreviousResponseId { get; set; }

    /// <summary>
    /// 将筛选参数转为字典，供 IObservabilityService 使用。可排除指定字段（用于筛选选项端点排除当前字段自身）。
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToFilters(string? excludedKey = null)
    {
        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddFilter(filters, "request_id", RequestId, excludedKey);
        AddFilter(filters, "model", Model, excludedKey);
        AddFilter(filters, "upstream_model", UpstreamModel, excludedKey);
        AddFilter(filters, "channel_id", ChannelId, excludedKey);
        AddFilter(filters, "owner_username", OwnerUsername, excludedKey);
        AddFilter(filters, "api_key_id", ApiKeyId, excludedKey);
        AddFilter(filters, "path", Path, excludedKey);
        AddFilter(filters, "request_type", RequestType, excludedKey);
        AddFilter(filters, "status_code", StatusCode, excludedKey);
        AddFilter(filters, "is_stream", IsStream, excludedKey);
        AddFilter(filters, "client_ip", ClientIp, excludedKey);
        AddFilter(filters, "error", Error, excludedKey);
        AddFilter(filters, "request_status", RequestStatus, excludedKey);
        AddFilter(filters, "created_from", CreatedFrom, excludedKey);
        AddFilter(filters, "created_to", CreatedTo, excludedKey);
        AddFilter(filters, "conversation_key", ConversationKey, excludedKey);
        AddFilter(filters, "conversation_turn_id", ConversationTurnId, excludedKey);
        AddFilter(filters, "conversation_window_id", ConversationWindowId, excludedKey);
        AddFilter(filters, "previous_response_id", PreviousResponseId, excludedKey);
        return filters;
    }

    private static void AddFilter(
        Dictionary<string, object?> filters,
        string key,
        string? value,
        string? excludedKey)
    {
        if (key == excludedKey || string.IsNullOrEmpty(value))
        {
            return;
        }

        filters[key] = value;
    }
}

/// <summary>
/// 表示统计查询的参数，在日志筛选基础上追加 range、start、end。
/// </summary>
public sealed class StatsQuery : LogFilterQuery
{
    public string Range { get; set; } = "1h";
    public string? Start { get; set; }
    public string? End { get; set; }
}
