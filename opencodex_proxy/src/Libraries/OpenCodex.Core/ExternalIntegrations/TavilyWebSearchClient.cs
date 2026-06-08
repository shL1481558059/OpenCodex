using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed class TavilyWebSearchClient : IWebSearchClient
{
    private const string TavilySearchUrl = "https://api.tavily.com/search";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;

    public TavilyWebSearchClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["search_depth"] = "basic",
            ["max_results"] = 5,
            ["include_answer"] = "basic",
            ["include_raw_content"] = false,
            ["include_usage"] = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, TavilySearchUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {key.Key}");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var raw = DecodeJsonOrText(body);
                var error = $"Tavily returned HTTP {(int)response.StatusCode}";
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
            return new WebSearchProviderResult(
                true,
                (int)response.StatusCode,
                ElapsedMilliseconds(started),
                null,
                null,
                SummaryFromRaw(rawObject),
                rawObject);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RequestError(started, "Tavily request timed out");
        }
        catch (HttpRequestException exception)
        {
            return RequestError(started, $"failed to reach Tavily: {exception.Message}");
        }
        catch (JsonException)
        {
            return RequestError(started, "Tavily returned invalid JSON");
        }
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
                    ["content"] = WebSearchPayload.StringValue(result, "content"),
                    ["score"] = WebSearchPayload.GetValue(result, "score")
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
