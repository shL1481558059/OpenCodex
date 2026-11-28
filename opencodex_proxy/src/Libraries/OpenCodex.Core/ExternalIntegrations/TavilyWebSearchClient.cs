using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.ProtocolConversion;

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

    public Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        CancellationToken cancellationToken)
        => SearchAsync(key, query, WebSearchExecutionOptions.Default, cancellationToken);

    public async Task<WebSearchProviderResult> SearchAsync(
        WebSearchProviderKey key,
        string query,
        WebSearchExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var captureMaxBytes = ProtocolConversionParameters.Current.SseMaxCaptureBytes;
        var overflowBlockBytes = ProtocolConversionParameters.Current.FlushBytes;
        _ = UpstreamJsonEvidence.MaxRawBytes(captureMaxBytes, overflowBlockBytes);
        var profile = WebSearchProviderProfile.For(options.ContextSize);
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["search_depth"] = profile.TavilySearchDepth,
            ["max_results"] = profile.TavilyMaxResults,
            ["include_answer"] = "basic",
            ["include_raw_content"] = false,
            ["include_usage"] = true
        };
        if (profile.TavilyChunksPerSource is int chunks)
        {
            payload["chunks_per_source"] = chunks;
        }

        if (options.AllowedDomains.Count > 0)
        {
            payload["include_domains"] = options.AllowedDomains.ToArray();
            payload["include_domains_mode"] = "restrict";
        }
        BoundedJsonResponseReader.ValidateRequest(payload, JavaScriptEncoder.UnsafeRelaxedJsonEscaping, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, TavilySearchUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {key.Key}");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            // Root C also bounds tool-provider JSON ingress. Raw remains parsed JSON, not wire evidence.
            var ownedUtf8 = await BoundedJsonResponseReader.ReadRawAsync(
                response, captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "tavily");
            var parsed = BoundedJsonResponseReader.Parse(
                response, ownedUtf8, captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "tavily");
            if (!response.IsSuccessStatusCode)
            {
                var error = $"Tavily returned HTTP {(int)response.StatusCode}";
                var errorSummary = new WebSearchSummary(string.Empty, [], error);
                BoundedJsonResponseReader.EnsureNormalized(errorSummary, response, ownedUtf8,
                    captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "tavily");
                return new WebSearchProviderResult(
                    false,
                    (int)response.StatusCode,
                    ElapsedMilliseconds(started),
                    "http_error",
                    error,
                    errorSummary,
                    parsed);
            }

            var rawObject = BoundedJsonResponseReader.RequireObject(
                parsed, response, ownedUtf8, captureMaxBytes, overflowBlockBytes, "tavily");
            var summary = SummaryFromRaw(rawObject, profile.TavilyMaxResults);
            // Summary is a second C-bounded parsed projection. Raw itself stays unchanged.
            BoundedJsonResponseReader.EnsureNormalized(summary, response, ownedUtf8,
                captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "tavily");

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
            return RequestError(started, "Tavily request timed out", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return RequestError(started, $"failed to reach Tavily: {exception.Message}", cancellationToken);
        }
    }

    private static WebSearchProviderResult RequestError(long started, string error, CancellationToken cancellationToken)
    {
        var summary = new WebSearchSummary(string.Empty, [], error);
        BoundedJsonResponseReader.EnsureParsedBound(summary, ProtocolConversionParameters.Current.SseMaxCaptureBytes,
            cancellationToken, "provider_network_error", "tavily");
        return new WebSearchProviderResult(
            false,
            null,
            ElapsedMilliseconds(started),
            "request_error",
            error,
            summary,
            null);
    }

    private static WebSearchSummary SummaryFromRaw(Dictionary<string, object?> raw, int resultLimit)
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
                // Equivalent to the old final Take, without materializing discarded mapped items.
                if (results.Count >= resultLimit) break;
            }
        }

        return new WebSearchSummary(WebSearchPayload.StringValue(raw, "answer"), results, null);
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
}
