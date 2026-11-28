using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.Core.Errors;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;

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
        WebSearchProviderKey key, string query, WebSearchProviderProfile profile, string? site,
        CancellationToken cancellationToken,
        Action<WebSearchProviderResult, HttpResponseMessage, ReadOnlyMemory<byte>>? onResponse = null)
    {
        var started = Stopwatch.GetTimestamp();
        var captureMaxBytes = ProtocolConversionParameters.Current.SseMaxCaptureBytes;
        var overflowBlockBytes = ProtocolConversionParameters.Current.FlushBytes;
        _ = UpstreamJsonEvidence.MaxRawBytes(captureMaxBytes, overflowBlockBytes);
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query, ["max_results"] = profile.KeenableMaxResults
        };
        if (profile.KeenableSnippetMaxLength is int snippetLength) payload["snippet_max_length"] = snippetLength;
        if (!string.IsNullOrEmpty(site)) payload["site"] = site;
        BoundedJsonResponseReader.ValidateRequest(payload, JavaScriptEncoder.UnsafeRelaxedJsonEscaping, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, KeenableSearchUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("x-api-key", key.Key);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            // Root C also bounds tool-provider JSON ingress; Raw is the unchanged parsed graph.
            var ownedUtf8 = await BoundedJsonResponseReader.ReadRawAsync(
                response, captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "keenable");
            var parsed = BoundedJsonResponseReader.Parse(
                response, ownedUtf8, captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "keenable");
            if (!response.IsSuccessStatusCode)
            {
                var error = $"Keenable returned HTTP {(int)response.StatusCode}";
                var errorSummary = new WebSearchSummary(string.Empty, [], error);
                BoundedJsonResponseReader.EnsureNormalized(errorSummary, response, ownedUtf8,
                    captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "keenable");
                var failed = new WebSearchProviderResult(false, (int)response.StatusCode, ElapsedMilliseconds(started),
                    "http_error", error, errorSummary, parsed);
                onResponse?.Invoke(failed, response, ownedUtf8);
                return failed;
            }

            var rawObject = BoundedJsonResponseReader.RequireObject(
                parsed, response, ownedUtf8, captureMaxBytes, overflowBlockBytes, "keenable");
            var summary = SummaryFromRaw(rawObject, profile.KeenableMaxResults);
            BoundedJsonResponseReader.EnsureNormalized(summary, response, ownedUtf8,
                captureMaxBytes, overflowBlockBytes, timeoutCts.Token, "keenable");
            var result = new WebSearchProviderResult(true, (int)response.StatusCode, ElapsedMilliseconds(started),
                null, null, summary, rawObject);
            onResponse?.Invoke(result, response, ownedUtf8);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RequestError(started, "Keenable request timed out", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return RequestError(started, $"failed to reach Keenable: {exception.Message}", cancellationToken);
        }
    }

    // The existing domain order/concurrency and ordinary provider-error merge policy remain.
    // Policy failures cannot be masked by another successful domain or a partial-success merge.
    private async Task<WebSearchProviderResult> SearchManyAsync(
        WebSearchProviderKey key, string query, WebSearchProviderProfile profile,
        IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var captureMaxBytes = ProtocolConversionParameters.Current.SseMaxCaptureBytes;
        var overflowBlockBytes = ProtocolConversionParameters.Current.FlushBytes;
        using var domainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(WebSearchProviderProfile.KeenableDomainConcurrency);
        ExceptionDispatchInfo? firstPolicyFailure = null;
        Dictionary<string, object?>? receivedWitness = null;
        // Exact aggregate shell plus the N-1 comma bytes. Each completed domain
        // contributes the same entry that MergeDomainResults will actually write.
        long rawBytes = ProtocolJsonWriter.Count(
            new Dictionary<string, object?> { ["domains"] = new List<object?>() }, audit: true, cancellationToken)
            + Math.Max(0, domains.Count - 1);
        if (rawBytes > captureMaxBytes)
            throw BoundedJsonResponseReader.ParsedFailure(captureMaxBytes, BoundedJsonResponseReader.CaptureLimitCode,
                "Keenable parsed aggregate metadata exceeds configured capture byte limit.", "domain_metadata", "keenable");
        var tasks = new Task<WebSearchProviderResult>[domains.Count];
        for (var index = 0; index < domains.Count; index++) tasks[index] = SearchDomainAsync(domains[index]);

        WebSearchProviderResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Sibling cancellation and callback cleanup must not replace the original typed evidence.
            Volatile.Read(ref firstPolicyFailure)?.Throw();
            throw;
        }
        var combined = MergeDomainResults(domains, results, profile.KeenableMaxResults, started);
        try
        {
            if (ProtocolJsonWriter.Count(combined.Raw, audit: true, cancellationToken) > captureMaxBytes
                || ProtocolJsonWriter.Count(combined.Summary, audit: true, cancellationToken) > captureMaxBytes)
                throw AggregateFailure(BoundedJsonResponseReader.CaptureLimitCode,
                    "Keenable parsed aggregate Raw or summary exceeds configured capture byte limit.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw AggregateFailure(BoundedJsonResponseReader.InvalidJsonCode,
                "Keenable parsed aggregate JSON cannot be represented by the proxy.");
        }
        return combined;

        UpstreamException AggregateFailure(string code, string message)
        {
            var witness = Volatile.Read(ref receivedWitness);
            if (witness is not null)
                return new UpstreamException(
                    message + " Received JSON evidence covers one actual domain response, not reconstructed aggregate wire.",
                    body: witness, channelId: "keenable", errorCode: code);
            return BoundedJsonResponseReader.ParsedFailure(captureMaxBytes, code, message,
                "domain_aggregate_without_wire", "keenable");
        }

        async Task<WebSearchProviderResult> SearchDomainAsync(string domain)
        {
            await gate.WaitAsync(domainCts.Token);
            try
            {
                var hasReceivedBody = false;
                var result = await SearchOneAsync(key, query, profile, domain, domainCts.Token,
                    (providerResult, response, ownedUtf8) =>
                    {
                        hasReceivedBody = true;
                        long entryBytes;
                        try
                        {
                            entryBytes = ProtocolJsonWriter.Count(DomainRawEntry(domain, providerResult),
                                audit: true, domainCts.Token);
                        }
                        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
                        {
                            throw BoundedJsonResponseReader.Failure(response, ownedUtf8, true, true,
                                captureMaxBytes, overflowBlockBytes, BoundedJsonResponseReader.InvalidJsonCode,
                                "Keenable parsed domain JSON cannot be represented in the aggregate; received evidence covers this domain only.",
                                "keenable");
                        }
                        if (Interlocked.Add(ref rawBytes, entryBytes) > captureMaxBytes)
                            throw BoundedJsonResponseReader.Failure(response, ownedUtf8, true, true,
                                captureMaxBytes, overflowBlockBytes, BoundedJsonResponseReader.CaptureLimitCode,
                                "Keenable parsed aggregate Raw exceeds configured capture byte limit; received evidence covers the offending domain only.",
                                "keenable");
                        if (Volatile.Read(ref receivedWitness) is null)
                            Interlocked.CompareExchange(ref receivedWitness,
                                UpstreamJsonEvidence.Create(ownedUtf8, response.Content.Headers.ContentLength,
                                    (int)response.StatusCode, true, true, captureMaxBytes, overflowBlockBytes), null);
                    });
                if (!hasReceivedBody)
                {
                    // Network error metadata has parsed provenance, not invented empty wire evidence.
                    var entryBytes = ProtocolJsonWriter.Count(DomainRawEntry(domain, result), audit: true, domainCts.Token);
                    if (Interlocked.Add(ref rawBytes, entryBytes) > captureMaxBytes)
                        throw AggregateFailure(BoundedJsonResponseReader.CaptureLimitCode,
                            "Keenable parsed aggregate network-error metadata exceeds configured capture byte limit.");
                }
                return result;
            }
            catch (Exception exception) when (BoundedJsonResponseReader.IsPolicyFailure(exception))
            {
                Interlocked.CompareExchange(ref firstPolicyFailure, ExceptionDispatchInfo.Capture(exception), null);
                domainCts.Cancel();
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static Dictionary<string, object?> DomainRawEntry(string domain, WebSearchProviderResult result)
        => new(StringComparer.Ordinal)
        {
            ["site"] = domain, ["ok"] = result.Ok, ["status_code"] = result.StatusCode,
            ["error"] = result.Error, ["raw"] = result.Raw
        };

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
            // Parsed provider values remain unchanged; this is not a concatenated wire reconstruction.
            ["domains"] = domains.Select((domain, index) => (object?)DomainRawEntry(domain, results[index])).ToList()
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

    private static WebSearchProviderResult RequestError(long started, string error, CancellationToken cancellationToken)
    {
        var summary = new WebSearchSummary(string.Empty, [], error);
        BoundedJsonResponseReader.EnsureParsedBound(summary, ProtocolConversionParameters.Current.SseMaxCaptureBytes,
            cancellationToken, "provider_network_error", "keenable");
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
                    ["content"] = WebSearchPayload.StringValue(result, "snippet"),
                    ["description"] = WebSearchPayload.StringValue(result, "description"),
                    ["published_at"] = WebSearchPayload.GetValue(result, "published_at"),
                    ["acquired_at"] = WebSearchPayload.GetValue(result, "acquired_at")
                });
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
