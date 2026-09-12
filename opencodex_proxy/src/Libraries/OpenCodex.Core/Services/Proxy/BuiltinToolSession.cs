using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.WebSearch;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.Proxy;

internal sealed class BuiltinToolSession(
    BuiltinToolRequestContext binding,
    IWebSearchToolExecutor executor,
    WebSearchContinuationStore history,
    string owner,
    string protocol,
    int timeoutSeconds,
    int? outputTokenBudget) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, (string Arguments, WebSearchToolResult Result)> _executed = new(StringComparer.Ordinal);
    private readonly List<WebSearchToolResult> _results = [];
    private readonly List<Dictionary<string, object?>> _rounds = [];
    private readonly List<object?> _output = [];
    private readonly HashSet<string> _outputIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _deadline;
    private string? _responseId;
    private bool _searchDisabled;
    private int _providerCalls;
    private int _invalidCalls;

    public BuiltinToolRequestContext Binding => binding;
    public IReadOnlyList<WebSearchToolResult> Results => _results;
    public Dictionary<string, object?> Usage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> AccountingUsage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?>? NextRequest { get; private set; }
    public Dictionary<string, object?>? ResponsePayload { get; private set; }
    public bool HasCalls => _results.Count > 0;
    public CancellationToken Token(CancellationToken fallback) => _deadline?.Token ?? fallback;
    public Dictionary<string, object?>? Details => HasCalls ? WebSearchSimulationLog.Build(_results, _rounds) : null;

    public static int? OutputTokenBudget(IReadOnlyDictionary<string, object?> payload) =>
        GetValue(payload, "max_output_tokens") switch
        {
            null => null,
            int value when value > 0 => value,
            long value when value is > 0 and <= int.MaxValue => (int)value,
            _ => throw new BadRequestException("max_output_tokens must be a positive integer")
        };

    public async IAsyncEnumerable<BuiltinToolProgress> ProcessRoundAsync(
        Dictionary<string, object?> request,
        Dictionary<string, object?> upstreamResponse,
        Dictionary<string, object?> response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NextRequest = null;
        ResponsePayload = response;
        var calls = WebSearchToolCallParser.ExtractToolCalls(upstreamResponse, protocol);
        var owned = calls.Where(call => call.Name == binding.WebSearchToolName).ToList();
        if (owned.Count == 0 && !HasCalls)
        {
            yield break;
        }

        _responseId ??= StringValue(response, "id");
        AddUsage(Usage, ObjectValue(response, "usage"));
        AddUsage(AccountingUsage, ObjectValue(upstreamResponse, "usage"));
        _rounds.Add(new Dictionary<string, object?>
        {
            ["iteration"] = _rounds.Count + 1,
            ["after_limit"] = _searchDisabled,
            ["request"] = DeepCopyObject(request),
            ["usage"] = DeepCopyObject(ObjectValue(response, "usage")),
            ["tool_calls"] = calls.Select(call => (object?)new Dictionary<string, object?>
            {
                ["id"] = call.Id, ["name"] = call.Name
            }).ToList()
        });
        var roundResults = new List<WebSearchToolResult>();
        var remainingTokens = outputTokenBudget is int budget ? budget - ToInt(GetValue(Usage, "output_tokens"), 0) : (int?)null;
        var complete = IsCompleteToolRound(upstreamResponse, response);
        if (owned.Count > 0 && !complete && StringValue(response, "status") is not ("incomplete" or "failed" or "cancelled"))
        {
            throw new UpstreamException("upstream returned an unfinished proxy web search call", ProxyHttpStatus.BadGateway);
        }
        if (owned.Count > 0 && complete && remainingTokens is not <= 0)
        {
            if (!binding.SearchAllowed || _searchDisabled)
            {
                throw new UpstreamException("upstream called a disabled proxy web search tool", ProxyHttpStatus.BadGateway);
            }
            if (owned.Count > 64)
            {
                throw new UpstreamException("upstream returned too many proxy web search calls", ProxyHttpStatus.BadGateway);
            }
            EnsureDeadline(cancellationToken);
            foreach (var call in owned)
            {
                Token(cancellationToken).ThrowIfCancellationRequested();
                if (StringValue(call.Raw, "id").Length == 0)
                {
                    throw new UpstreamException("proxy web search call is missing its call id", ProxyHttpStatus.BadGateway);
                }
                if (_executed.TryGetValue(call.Id, out var previous))
                {
                    if (previous.Arguments != call.Arguments)
                    {
                        throw new UpstreamException("upstream reused a web search call id with different arguments", ProxyHttpStatus.BadGateway);
                    }
                    if (!roundResults.Contains(previous.Result))
                    {
                        roundResults.Add(previous.Result);
                    }
                    continue;
                }

                var itemId = $"{WebSearchRequestPolicy.PublicItemPrefix}{Guid.NewGuid():N}";
                var query = WebSearchRequestPolicy.ParseQuery(call.Arguments).Query ?? string.Empty;
                yield return new BuiltinToolProgress(itemId, query, null);
                binding.HasExecuted = true;
                WebSearchToolResult result;
                try
                {
                    result = _searchDisabled || _providerCalls >= binding.MaxWebSearchCalls
                        || _rounds.Count >= binding.MaxWebSearchCalls + 3
                        ? WebSearchToolResult.Failed(call.Id, query, "Further web search calls are disabled for this response.", true)
                        : await executor.ExecuteAsync(call.Id, call.Arguments, Token(cancellationToken));
                }
                catch (Exception exception)
                {
                    var failed = WebSearchToolResult.Failed(call.Id, query,
                        exception is OperationCanceledException ? "Web search was cancelled." : "Web search execution failed.", true);
                    failed.ItemId = itemId;
                    _executed.Add(call.Id, (call.Arguments, failed));
                    _results.Add(failed);
                    if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        throw new UpstreamException("proxy web search request timed out", ProxyHttpStatus.GatewayTimeout);
                    }
                    throw;
                }
                result.ItemId = itemId;
                _executed.Add(call.Id, (call.Arguments, result));
                _results.Add(result);
                roundResults.Add(result);
                if (result.KeyId is not null || result.Status == "completed")
                {
                    _providerCalls++;
                }
                if (result.Status != "completed")
                {
                    _invalidCalls++;
                }
                _searchDisabled |= result.DisableSearch || _invalidCalls >= 2 || _providerCalls >= binding.MaxWebSearchCalls;
                yield return new BuiltinToolProgress(itemId, query, result);
            }
        }

        var projected = WebSearchResponsePayload.ProjectRound(response, roundResults, binding.WebSearchToolName, binding.IncludeSources);
        foreach (var value in ListValue(projected, "output"))
        {
            var id = TryAsObject(value, out var item) ? StringValue(item, "id") : string.Empty;
            if (id.Length == 0 || _outputIds.Add(id))
            {
                _output.Add(value);
            }
        }
        projected["id"] = _responseId;
        projected["output"] = _output.ToList();
        projected["usage"] = DeepCopyObject(Usage);
        ResponsePayload = WebSearchResponsePayload.AddSourceAnnotations(projected, _results);

        if (remainingTokens is <= 0 && owned.Count > 0)
        {
            ResponsePayload["status"] = "incomplete";
            ResponsePayload["incomplete_details"] = new Dictionary<string, object?> { ["reason"] = "max_output_tokens" };
            yield break;
        }
        if (!complete || owned.Count == 0)
        {
            yield break;
        }

        var clientCalls = calls.Where(call => call.Name != binding.WebSearchToolName).ToList();
        await history.SaveAsync(owner, roundResults, clientCalls, Token(cancellationToken));
        if (clientCalls.Count > 0)
        {
            yield break;
        }

        _searchDisabled |= _rounds.Count >= binding.MaxWebSearchCalls + 3;
        NextRequest = WebSearchContinuationRequest.AppendToolResults(
            request, upstreamResponse, protocol, roundResults, _searchDisabled, binding.WebSearchToolName);
        if (remainingTokens is int remaining)
        {
            NextRequest[NextRequest.ContainsKey("max_completion_tokens") ? "max_completion_tokens" : "max_tokens"] = remaining;
        }
    }

    private void EnsureDeadline(CancellationToken cancellationToken)
    {
        if (_deadline is not null)
        {
            return;
        }
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) - Stopwatch.GetElapsedTime(_started);
        if (remaining <= TimeSpan.Zero)
        {
            _deadline.Cancel();
            throw new UpstreamException("proxy web search request timed out", ProxyHttpStatus.GatewayTimeout);
        }
        _deadline.CancelAfter(remaining);
    }

    private bool IsCompleteToolRound(Dictionary<string, object?> upstream, Dictionary<string, object?> response)
    {
        if (StringValue(response, "status") is "incomplete" or "failed" or "cancelled")
        {
            return false;
        }
        var stopReason = protocol == ProtocolConverter.Chat
            ? StringValue(FirstObject(ListValue(upstream, "choices")) ?? [], "finish_reason")
            : StringValue(upstream, "stop_reason");
        return stopReason is "tool_calls" or "tool_use" or "stop" or "end_turn";
    }

    private static void AddUsage(Dictionary<string, object?> total, Dictionary<string, object?> usage)
    {
        foreach (var (key, value) in usage)
        {
            if (TryAsObject(value, out var details))
            {
                var target = ObjectValue(total, key);
                AddUsage(target, details);
                total[key] = target;
            }
            else if (ReadUsageCount(value) is long count)
            {
                try
                {
                    total[key] = checked(Convert.ToInt64(GetValue(total, key) ?? 0L) + count);
                }
                catch (OverflowException)
                {
                    throw new UpstreamException($"upstream usage '{key}' exceeds the supported range", ProxyHttpStatus.BadGateway);
                }
            }
            else if (value is not null && key.EndsWith("_tokens", StringComparison.Ordinal))
            {
                throw new UpstreamException($"upstream usage '{key}' must be a non-negative integer", ProxyHttpStatus.BadGateway);
            }
        }
    }

    private static long? ReadUsageCount(object? value) => value switch
    {
        int count when count >= 0 => count,
        long count when count >= 0 => count,
        double count when double.IsFinite(count) && count >= 0
            && count < 9_223_372_036_854_775_808d && Math.Truncate(count) == count => (long)count,
        decimal count when count >= 0 && count <= long.MaxValue && decimal.Truncate(count) == count => (long)count,
        _ => null
    };

    public void Dispose() => _deadline?.Dispose();
}

internal sealed class BuiltinToolProgress(string itemId, string query, WebSearchToolResult? result)
{
    public string ItemId { get; } = itemId;
    public string Query { get; } = query;
    public WebSearchToolResult? Result { get; } = result;
}
