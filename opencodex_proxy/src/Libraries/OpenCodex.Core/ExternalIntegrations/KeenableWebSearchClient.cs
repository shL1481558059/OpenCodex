using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed class KeenableWebSearchClient : IWebSearchClient
{
    private const string KeenableSearchUrl = "https://api.keenable.ai/v1/search";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;

    public KeenableWebSearchClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken)
        => SearchAsync(key, query, WebSearchExecutionOptions.Default, cancellationToken);

    public Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        WebSearchExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var profile = WebSearchProviderProfile.For(options.ContextSize);
        return options.AllowedDomains.Count <= 1
            ? SearchOneAsync(
                key,
                query,
                profile,
                options.AllowedDomains.Count == 1 ? options.AllowedDomains[0] : null,
                cancellationToken)
            : SearchManyAsync(key, query, profile, options.AllowedDomains, cancellationToken);
    }

    private async Task<WebSearchProviderResult> SearchOneAsync(
        WebSearchProviderKey key,
        string query,
        WebSearchProviderProfile profile,
        string? site,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["max_results"] = profile.KeenableMaxResults
        };
        if (profile.KeenableSnippetMaxLength is int snippetLength)
        {
            payload["snippet_max_length"] = snippetLength;
        }

        if (!string.IsNullOrEmpty(site))
        {
            payload["site"] = site;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, KeenableSearchUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("x-api-key", key.Key);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var raw = DecodeJsonOrText(body);
                var error = $"Keenable returned HTTP {(int)response.StatusCode}";
                return new WebSearchProviderResult(
                    false,
                    (int)response.StatusCode,
                    ElapsedMilliseconds(started),
                    "http_error",
                    error,
                    new WebSearchSummary(string.Empty, [], error),
                    raw);
            }

            var rawObject = DecodeJsonObject(body);
            var summary = SummaryFromRaw(rawObject);
            if (summary.Results.Count > profile.KeenableMaxResults)
            {
                summary = new WebSearchSummary(
                    summary.Answer,
                    summary.Results.Take(profile.KeenableMaxResults).ToList(),
                    summary.Error);
            }

            return new WebSearchProviderResult(
                true,
                (int)response.StatusCode,
                ElapsedMilliseconds(started),
                null,
                null,
                summary,
                rawObject);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RequestError(started, "Keenable request timed out");
        }
        catch (HttpRequestException exception)
        {
            return RequestError(started, $"failed to reach Keenable: {exception.Message}");
        }
        catch (JsonException)
        {
            return RequestError(started, "Keenable returned invalid JSON");
        }
    }

    // Keenable 只有单个 site。多个域名各请求一次，按声明顺序去重合并。
    // 只要有一个域名成功就返回成功结果；全部失败才失败。
    private async Task<WebSearchProviderResult> SearchManyAsync(
        WebSearchProviderKey key,
        string query,
        WebSearchProviderProfile profile,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var gate = new SemaphoreSlim(WebSearchProviderProfile.KeenableDomainConcurrency);
        var tasks = new Task<WebSearchProviderResult>[domains.Count];
        for (var index = 0; index < domains.Count; index++)
        {
            var domain = domains[index];
            tasks[index] = SearchDomainAsync(domain);
        }

        WebSearchProviderResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        return MergeDomainResults(domains, results, profile.KeenableMaxResults, started);

        async Task<WebSearchProviderResult> SearchDomainAsync(string domain)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await SearchOneAsync(key, query, profile, domain, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static WebSearchProviderResult MergeDomainResults(
        IReadOnlyList<string> domains,
        IReadOnlyList<WebSearchProviderResult> results,
        int resultLimit,
        long started)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<Dictionary<string, object?>>();
        string answer = string.Empty;
        var failures = new List<WebSearchProviderResult>();
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (!result.Ok)
            {
                failures.Add(result);
                continue;
            }

            if (answer.Length == 0 && result.Summary.Answer.Length > 0)
            {
                answer = result.Summary.Answer;
            }

            foreach (var item in result.Summary.Results)
            {
                var url = WebSearchPayload.StringValue(item, "url").Trim();
                if (url.Length > 0 && !seen.Add(url))
                {
                    continue;
                }

                merged.Add(item);
                if (merged.Count >= resultLimit)
                {
                    break;
                }
            }

            if (merged.Count >= resultLimit)
            {
                break;
            }
        }

        var raw = new Dictionary<string, object?>
        {
            ["domains"] = domains.Select((domain, index) => (object?)new Dictionary<string, object?>
            {
                ["site"] = domain,
                ["ok"] = results[index].Ok,
                ["status_code"] = results[index].StatusCode,
                ["error"] = results[index].Error,
                ["raw"] = results[index].Raw
            }).ToList()
        };
        if (failures.Count == results.Count)
        {
            var first = failures[0];
            var error = $"Keenable search failed for all {domains.Count} domains: {first.Error}";
            return new WebSearchProviderResult(
                false,
                first.StatusCode,
                ElapsedMilliseconds(started),
                first.ErrorType,
                error,
                new WebSearchSummary(string.Empty, [], error),
                raw);
        }

        return new WebSearchProviderResult(
            true,
            200,
            ElapsedMilliseconds(started),
            null,
            null,
            new WebSearchSummary(answer, merged, null),
            raw);
    }

    private static WebSearchProviderResult RequestError(long started, string error)
    {
        return new WebSearchProviderResult(
            false,
            null,
            ElapsedMilliseconds(started),
            "request_error",
            error,
            new WebSearchSummary(string.Empty, [], error),
            null);
    }

    private static WebSearchSummary SummaryFromRaw(Dictionary<string, object?> raw)
    {
        var results = new List<Dictionary<string, object?>>();
        if (WebSearchPayload.TryAsList(WebSearchPayload.GetValue(raw, "results"), out var rawResults))
        {
            foreach (var item in rawResults)
            {
                if (!WebSearchPayload.TryAsObject(item, out var result))
                {
                    continue;
                }

                results.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["title"] = WebSearchPayload.StringValue(result, "title"),
                    ["url"] = WebSearchPayload.StringValue(result, "url"),
                    ["content"] = WebSearchPayload.StringValue(result, "snippet"),
                    ["description"] = WebSearchPayload.StringValue(result, "description"),
                    ["published_at"] = WebSearchPayload.GetValue(result, "published_at"),
                    ["acquired_at"] = WebSearchPayload.GetValue(result, "acquired_at")
                });
            }
        }

        return new WebSearchSummary(WebSearchPayload.StringValue(raw, "answer"), results, null);
    }

    private static object? DecodeJsonOrText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return WebSearchPayload.FromJsonElement(document.RootElement);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static Dictionary<string, object?> DecodeJsonObject(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? (Dictionary<string, object?>)WebSearchPayload.FromJsonElement(document.RootElement)!
            : [];
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
}
