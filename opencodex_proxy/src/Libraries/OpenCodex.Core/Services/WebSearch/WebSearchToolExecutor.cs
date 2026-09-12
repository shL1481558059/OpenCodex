using Microsoft.EntityFrameworkCore;
using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.WebSearch;

public sealed class WebSearchToolExecutor(
    IWebSearchClient client,
    IRepository<WebSearchSettings> settings,
    IRepository<TavilyKey> keys) : IWebSearchToolExecutor
{
    public string CurrentMode()
    {
        var mode = settings.TableNoTracking.Select(item => item.Mode).FirstOrDefault()
            ?.Trim().ToLowerInvariant() ?? WebSearchModes.Convert;
        return WebSearchModes.IsValid(mode) ? mode : WebSearchModes.Convert;
    }

    public async Task<WebSearchToolResult> ExecuteAsync(
        string callId,
        string arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (query, error) = WebSearchRequestPolicy.ParseQuery(arguments);
        if (error is not null)
        {
            return WebSearchToolResult.Failed(callId, string.Empty, error);
        }

        var key = await ReserveKeyAsync(cancellationToken);
        if (key is null)
        {
            return WebSearchToolResult.Failed(callId, query!, "Web search is unavailable or its quota is exhausted.", true);
        }

        var result = await client.SearchAsync(new WebSearchProviderKey(key.Provider, key.Key), query!, cancellationToken);
        var bounded = new WebSearchProviderResult(
            result.Ok, result.StatusCode, result.DurationMs, result.ErrorType, result.Error,
            new WebSearchSummary(
                LimitText(result.Summary.Answer, 4096),
                result.Summary.Results.Take(5).Select(source => new Dictionary<string, object?>
                {
                    ["title"] = LimitText(WebSearchPayload.StringValue(source, "title"), 512),
                    ["url"] = LimitText(WebSearchPayload.StringValue(source, "url"), 2048),
                    ["content"] = LimitText(WebSearchPayload.StringValue(source, "content"), 4096),
                    ["score"] = WebSearchPayload.GetValue(source, "score")
                }).ToList(),
                result.Summary.Error),
            result.Raw);
        return WebSearchToolResult.FromProvider(callId, query!, key, bounded);
    }

    private async Task<TavilyKeyDto?> ReserveKeyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var key = await keys.TableNoTracking
                .Where(item => item.Enabled && item.UsageCount < item.UsageLimit)
                .OrderBy(item => item.Position).ThenBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (key is null)
            {
                return null;
            }

            var updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            // Compare-and-swap keeps the reserved usage snapshot accurate across concurrent requests.
            var changed = await keys.Table
                .Where(item => item.Id == key.Id && item.Enabled
                    && item.UsageCount == key.UsageCount && item.UsageCount < item.UsageLimit)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.UsageCount, item => item.UsageCount + 1)
                    .SetProperty(item => item.UpdatedAt, updatedAt), cancellationToken);
            if (changed == 1)
            {
                return new TavilyKeyDto(
                    key.Id, key.Position, key.Provider, key.ApiKey, key.Enabled,
                    key.UsageCount + 1, key.UsageLimit, key.UsageLimit);
            }
        }

        return null;
    }

    private static string LimitText(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
    }
}
