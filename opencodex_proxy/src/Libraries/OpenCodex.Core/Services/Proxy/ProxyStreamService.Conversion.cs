using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.Proxy;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.Proxy;

public sealed partial class ProxyStreamService
{
    private async IAsyncEnumerable<string> ConvertRoundsAsync(
        ProxyStreamContext context,
        List<ProxyRequestStreamLineCapture> captures,
        ConvertedProxyStreamState state,
        BuiltinToolSession? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamState = tools is null ? null : new WebSearchStreamEventState();
        var skipped = tools is null ? null : new HashSet<string>([tools.Binding.WebSearchToolName], StringComparer.Ordinal);
        var textFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload);
        var mappings = context.EntryProtocol == ProtocolConverter.Responses
            && context.ChannelType is ProtocolConverter.Chat or ProtocolConverter.Messages
            ? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
            : null;

        while (true)
        {
            var token = tools?.Token(cancellationToken) ?? cancellationToken;
            var converted = new ConvertedStreamResult { TextFormat = textFormat, ToolCallMappings = mappings };
            var upstreamLines = _upstream.StreamJsonAsync(context.Route.Channel, state.UpstreamRequest, context.DefaultTimeout, token);
            var confirmed = await UpstreamStreamPrimer.PrimeAsync(
                CaptureStreamLines(upstreamLines, captures, "upstream", token), token);
            var lines = ConvertRound(context, confirmed, converted, streamState, skipped, token);
            string? terminal = null;
            await foreach (var line in lines.WithCancellation(token))
            {
                if (streamState is null)
                {
                    yield return line;
                }
                else if (WebSearchStreamEventState.IsTerminal(line))
                {
                    terminal = line;
                }
                else
                {
                    yield return streamState.Observe(line, tools!.Results);
                }
            }

            state.UpstreamResponse = converted.UpstreamResponse;
            if (tools is not null && !converted.UpstreamCompleted)
            {
                throw new UpstreamException("upstream stream ended before its terminal event", ProxyHttpStatus.BadGateway);
            }
            if (converted.UpstreamResponse is null)
            {
                yield break;
            }
            var response = ProtocolConverter.ConvertResponse(
                converted.UpstreamResponse, context.EntryProtocol, context.ChannelType,
                context.Route.OriginalModel, textFormat, mappings);
            state.ResponsePayload = response;
            if (tools is null)
            {
                yield break;
            }
            if (terminal is not null)
            {
                var (_, terminalPayload) = WebSearchResponsePayload.ParseSseLine(terminal);
                if (TryAsObject(GetValue(ObjectValue(terminalPayload ?? [], "response"), "usage"), out var usage))
                {
                    // Preserve usage details already normalized by the stream converter.
                    response["usage"] = usage;
                }
            }

            await foreach (var progress in tools.ProcessRoundAsync(
                state.UpstreamRequest, converted.UpstreamResponse, response, cancellationToken))
            {
                if (progress.Result is null)
                {
                    yield return streamState!.EmitWebSearchAdded(progress.ItemId, progress.Query, out _);
                    yield return streamState.EmitWebSearchInProgress(progress.ItemId);
                    yield return streamState.EmitWebSearchSearching(progress.ItemId);
                }
                else
                {
                    if (progress.Result.Status == "completed")
                    {
                        yield return streamState!.EmitWebSearchCompleted(progress.ItemId);
                    }
                    yield return streamState!.EmitWebSearchDone(
                        streamState.SearchIndex(progress.ItemId), progress.Result, tools.Binding.IncludeSources);
                }
            }
            state.ResponsePayload = tools.ResponsePayload;
            if (tools.NextRequest is { } next)
            {
                state.UpstreamRequest = next;
                continue;
            }
            if (terminal is not null)
            {
                var final = streamState!.Complete(
                    terminal, tools.HasCalls ? tools.Usage : null, tools.ResponsePayload, out var finalResponse);
                state.ResponsePayload = finalResponse;
                yield return final;
            }
            yield break;
        }
    }

    private static IAsyncEnumerable<string> ConvertRound(
        ProxyStreamContext context,
        IAsyncEnumerable<string> lines,
        ConvertedStreamResult converted,
        WebSearchStreamEventState? state,
        IReadOnlySet<string>? skipped,
        CancellationToken cancellationToken)
    {
        var model = VisibleModel(context);
        var includeUsage = context.Payload.TryGetValue("stream_options", out var options)
            && options is Dictionary<string, object?> streamOptions
            && streamOptions.TryGetValue("include_usage", out var usage) && usage is true;
        return (context.EntryProtocol, context.ChannelType) switch
        {
            (ProtocolConverter.Responses, ProtocolConverter.Chat) => SseStreamConverter.ChatToResponsesEvents(
                lines, model, converted, skipped, state?.HasResponse == true,
                state?.SequenceNumber ?? 0, state?.NextOutputIndex ?? 0, cancellationToken),
            (ProtocolConverter.Responses, ProtocolConverter.Messages) => SseStreamConverter.MessagesToResponsesEvents(
                lines, model, converted, skipped, state?.HasResponse == true,
                state?.SequenceNumber ?? 0, state?.NextOutputIndex ?? 0, cancellationToken),
            (ProtocolConverter.Messages, ProtocolConverter.Chat) => SseStreamConverter.ChatToMessagesEvents(
                lines, model, converted, null, false, cancellationToken),
            (ProtocolConverter.Chat, ProtocolConverter.Messages) => SseStreamConverter.MessagesToChatEvents(
                lines, model, converted, null, includeUsage, cancellationToken),
            (ProtocolConverter.Chat, ProtocolConverter.Responses) => SseStreamConverter.ResponsesToChatEvents(
                lines, model, converted, null, cancellationToken),
            (ProtocolConverter.Messages, ProtocolConverter.Responses) => SseStreamConverter.ResponsesToMessagesEvents(
                lines, model, converted, null, false, cancellationToken),
            _ => throw new BadRequestException($"streaming conversion not implemented for {context.EntryProtocol} to {context.ChannelType}")
        };
    }

    private sealed class ConvertedProxyStreamState
    {
        public required Dictionary<string, object?> UpstreamRequest { get; set; }
        public Dictionary<string, object?>? UpstreamResponse { get; set; }
        public Dictionary<string, object?>? ResponsePayload { get; set; }
    }
}
