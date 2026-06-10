using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;

namespace OpenCodex.Core.Services.Proxy;

public sealed class ProxyStreamService : IProxyStreamService
{
    private readonly IUpstreamClient _upstream;
    private readonly IProxyLogService _logs;
    private readonly IWebSearchSimulator _webSearch;

    public ProxyStreamService(
        IUpstreamClient upstream,
        IProxyLogService logs,
        IWebSearchSimulator webSearch)
    {
        _upstream = upstream;
        _logs = logs;
        _webSearch = webSearch;
    }

    public async Task StreamAsync(ProxyStreamContext context)
    {
        context.UpstreamRequest["stream"] = true;
        context.StreamWriter.PrepareSse();

        var ttftStarted = Stopwatch.GetTimestamp();
        var ttftMs = (int?)null;
        var error = (string?)null;
        Dictionary<string, object?>? webSearchDetails = null;
        Dictionary<string, object?>? upstreamResponse = null;
        Dictionary<string, object?>? responsePayload = null;
        var upstreamRequest = context.UpstreamRequest;

        try
        {
            if (_webSearch.CanSimulate(
                context.EntryProtocol,
                context.ChannelType,
                context.OwnerRole,
                context.Payload)
                && context.ChannelType == ProtocolConverter.Chat)
            {
                var streamResult = new WebSearchStreamResult();
                var visibleModel = VisibleModel(context);
                var streamLines = _webSearch.RunChatStreamAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.Payload,
                    visibleModel,
                    context.DefaultTimeout,
                    streamResult,
                    context.CancellationToken);
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    streamLines,
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);

                upstreamRequest = streamResult.FinalUpstreamRequest ?? upstreamRequest;
                upstreamResponse = streamResult.FinalUpstreamResponse;
                responsePayload = streamResult.ResponsePayload;
                webSearchDetails = streamResult.Details;
            }
            else if (context.EntryProtocol == context.ChannelType)
            {
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var capture = new PassThroughCapture();
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    CaptureStreamUsage(streamLines, capture, context.CancellationToken),
                    static line => line.Trim().Length > 0,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);
                upstreamResponse = capture.UpstreamResponse;
            }
            else
            {
                var converted = new ConvertedStreamResult();
                var visibleModel = VisibleModel(context);
                var streamLines = _upstream.StreamJsonAsync(
                    context.Route.Channel,
                    upstreamRequest,
                    context.DefaultTimeout,
                    context.CancellationToken);
                var convertedLines = context.ChannelType == ProtocolConverter.Chat
                    ? SseStreamConverter.ChatToResponsesEvents(
                        streamLines,
                        visibleModel,
                        converted,
                        context.CancellationToken)
                    : SseStreamConverter.MessagesToResponsesEvents(
                        streamLines,
                        visibleModel,
                        converted,
                        context.CancellationToken);
                ttftMs = await context.StreamWriter.WriteLinesAsync(
                    convertedLines,
                    SseStreamConverter.CountsForTtft,
                    () => ElapsedMilliseconds(ttftStarted),
                    context.CancellationToken);

                upstreamResponse = converted.UpstreamResponse;
                responsePayload = upstreamResponse is null
                    ? null
                    : ProtocolConverter.ConvertResponse(
                        upstreamResponse,
                        context.EntryProtocol,
                        context.ChannelType,
                        context.Route.OriginalModel);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            throw;
        }
        finally
        {
            _logs.WriteLog(
                new ProxyLogContext(
                    context.RequestId,
                    context.OwnerUsername,
                    context.ApiKeyId,
                    context.OriginalPayload,
                    upstreamRequest,
                    upstreamResponse,
                    responsePayload,
                    ErrorResponse: null,
                    context.RequestModel,
                    context.UpstreamModel,
                    context.ChannelId,
                    context.ChannelType,
                    IsStream: true,
                    TtftMs: ttftMs,
                    StatusCode: 200,
                    DurationMs: ElapsedMilliseconds(context.StartedTimestamp),
                    error,
                    webSearchDetails),
                context.RequestMetadata);
        }
    }

    private static string? VisibleModel(ProxyStreamContext context)
    {
        return context.Route.OriginalModel.Length > 0
            ? context.Route.OriginalModel
            : context.RequestModel;
    }

    private static int ElapsedMilliseconds(long started)
    {
        return (int)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            MidpointRounding.AwayFromZero);
    }
    internal sealed class PassThroughCapture
    {
        public Dictionary<string, object?>? UpstreamResponse { get; set; }
    }

    internal static async IAsyncEnumerable<string> CaptureStreamUsage(
        IAsyncEnumerable<string> lines,
        PassThroughCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? model = null;
        object? usage = null;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            yield return line;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var json = line["data:".Length..].TrimStart();
            if (json.Length == 0 || json == "[DONE]")
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                model ??= TryExtractString(root, "model");
                usage ??= TryExtractObject(root, "usage");
                if (root.TryGetProperty("response", out var responseEl)
                    && responseEl.ValueKind == JsonValueKind.Object)
                {
                    model ??= TryExtractString(responseEl, "model");
                    usage ??= TryExtractObject(responseEl, "usage");
                }
                if (root.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.Object)
                {
                    model ??= TryExtractString(messageEl, "model");
                    usage ??= TryExtractObject(messageEl, "usage");
                }
            }
            catch (JsonException)
            {
            }
        }
        capture.UpstreamResponse = model is null && usage is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["usage"] = usage ?? new Dictionary<string, object?>()
            };
    }
    internal static string? TryExtractString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } str
            ? str
            : null;
    }
    internal static object? TryExtractObject(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return FromJsonElement(value);
    }
    internal static object? FromJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                p => p.Name,
                p => FromJsonElement(p.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? (l is >= int.MinValue and <= int.MaxValue ? (int)l : l)
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
