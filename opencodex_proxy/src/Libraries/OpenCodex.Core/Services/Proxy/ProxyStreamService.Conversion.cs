using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.Proxy;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.Proxy;

public sealed partial class ProxyStreamService
{
    private async IAsyncEnumerable<SseFrame> ConvertRoundsAsync(
        ProxyStreamContext context,
        ConvertedProxyStreamState state,
        BuiltinToolSession? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamState = tools is null ? null : new WebSearchStreamEventState();
        var skipped = tools is null ? null : new HashSet<string>([tools.Binding.WebSearchToolName], StringComparer.Ordinal);
        var textFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload);
        var mappings = context.EntryProtocol == ProtocolConverter.Responses
            && context.ChannelType is ProtocolConverter.Chat or ProtocolConverter.Messages
            ? context.ToolCallMappings ?? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
            : null;
        var shouldBuffer = tools is not null || mappings is { Count: > 0 };
        var retryUsed = false;

        while (true)
        {
            var token = tools?.Token(cancellationToken) ?? cancellationToken;
            var knownNames=new HashSet<string>(skipped ?? (IEnumerable<string>)Array.Empty<string>(),StringComparer.Ordinal);
            if(mappings is not null)
            {
                knownNames.UnionWith(mappings.Keys);
                foreach(var mapping in mappings.Values)
                    if(ProtocolConverter.TryGetResponsesToolCallMapping(mapping.ResponsesName,mappings,out _))knownNames.Add(mapping.ResponsesName);
            }
            using var capture=CreateFullCapture(context.ChannelType,token,knownNames);
            var converted = new ConvertedStreamResult { TextFormat = textFormat, ToolCallMappings = mappings,SharedCapture=capture };
            var upstreamLines = _upstream.StreamEventsAsync(context.Route.Channel, state.UpstreamRequest, context.DefaultTimeout, token);
            var confirmed = await UpstreamStreamPrimer.PrimeAsync(
                upstreamLines, token);
            var validated = ValidateUpstreamCompletion(confirmed, capture, token);
            var lines = ConvertRound(context, validated, converted, streamState, skipped, token);
            SseFrame? terminal = null;
            if (!shouldBuffer)
            {
                try
                {
                    await foreach (var line in lines.WithCancellation(token))
                    {
                        yield return line;
                    }
                }
                finally
                {
                    state.UpstreamResponse = converted.UpstreamResponse
                        ?? capture.Complete(capture.IsComplete
                            ? StreamCaptureTermination.Completed
                            : StreamCaptureTermination.UnexpectedEnd).Response;
                }
            }
            else
            {
                Queue<SseFrame> buffered = new();
                var retryExhausted = false;
                try
                {
                    buffered = await CollectFramesAsync(lines, _outputFrames, token);
                }
                catch (UpstreamException exception)
                    when (ToolContractRetryPolicy.IsUndeclaredToolCall(exception))
                {
                    var rejectedResponse = converted.UpstreamResponse
                        ?? capture.Complete(capture.IsComplete
                            ? StreamCaptureTermination.Completed
                            : StreamCaptureTermination.UnexpectedEnd).Response;
                    if (tools is not null)
                    {
                        tools.RecordRejectedRound(rejectedResponse, exception.Message);
                    }
                    else
                    {
                        BuiltinToolSession.AddUsage(
                            state.RejectedUsage,
                            rejectedResponse is null
                                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                                : ObjectValue(rejectedResponse, "usage"));
                    }

                    if (retryUsed)
                    {
                        if (tools is null || !streamState!.HasResponse)
                        {
                            throw ToolContractRetryPolicy.RetryExhausted(exception);
                        }

                        retryExhausted = true;
                    }
                    else
                    {
                        retryUsed = true;
                        state.UpstreamRequest = ToolContractRetryPolicy.BuildRetryRequest(
                            state.UpstreamRequest,
                            context.ChannelType,
                            mappings?.Keys ?? Array.Empty<string>(),
                            tools?.Binding.WebSearchToolName);
                        state.UpstreamRequestBody = ProxyLogBodyHandle.CaptureJson(state.UpstreamRequest);
                        continue;
                    }
                }
                finally
                {
                    state.UpstreamResponse = converted.UpstreamResponse
                        ?? capture.Complete(capture.IsComplete
                            ? StreamCaptureTermination.Completed
                            : StreamCaptureTermination.UnexpectedEnd).Response;
                }

                if (retryExhausted)
                {
                    var failed = streamState!.FailFrame(
                        "upstream_tool_contract_error",
                        "The upstream model repeatedly returned an undeclared tool name.",
                        out var finalResponse);
                    state.ResponsePayload = finalResponse;
                    yield return failed;
                    yield break;
                }

                while (buffered.TryDequeue(out var line))
                {
                    if (tools is null)
                    {
                        yield return line;
                    }
                    else if (WebSearchStreamEventState.IsTerminal(line))
                    {
                        terminal = line;
                    }
                    else
                    {
                        yield return streamState!.Observe(line, tools.Results);
                    }
                }
            }

            if (converted.UpstreamResponse is null)
            {
                yield break;
            }
            var response = ProtocolConverter.ConvertResponse(
                converted.UpstreamResponse, context.EntryProtocol, context.ChannelType,
                context.Route.OriginalModel, textFormat, mappings);
            state.ResponsePayload = response;
            if (tools is null && state.RejectedUsage.Count > 0)
            {
                BuiltinToolSession.AddUsage(
                    state.RejectedUsage,
                    ObjectValue(converted.UpstreamResponse, "usage"));
            }
            if (HasStreamFailure(converted.UpstreamResponse))
            {
                if (terminal is not null)
                {
                    yield return terminal;
                }
                yield break;
            }
            if (tools is null)
            {
                yield break;
            }
            if (terminal is not null)
            {
                var terminalPayload = terminal.Event?.Data as Dictionary<string, object?>;
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
                    yield return streamState!.EmitWebSearchAddedFrame(progress.ItemId, progress.Query, out _);
                    yield return streamState.EmitWebSearchInProgressFrame(progress.ItemId);
                    yield return streamState.EmitWebSearchSearchingFrame(progress.ItemId);
                }
                else
                {
                    if (progress.Result.Status == "completed")
                    {
                        yield return streamState!.EmitWebSearchCompletedFrame(progress.ItemId);
                    }
                    yield return streamState!.EmitWebSearchDoneFrame(
                        streamState.SearchIndex(progress.ItemId), progress.Result, tools.Binding.IncludeSources);
                }
            }
            state.ResponsePayload = tools.ResponsePayload;
            if (tools.NextRequest is { } next)
            {
                state.UpstreamRequest = next;
                state.UpstreamRequestBody = ProxyLogBodyHandle.CaptureJson(next);
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

    internal static async Task<Queue<SseFrame>> CollectFramesAsync(
        IAsyncEnumerable<SseFrame> lines, SseFrameOptions options,
        CancellationToken cancellationToken)
    {
        var result = new Queue<SseFrame>();
        long bytes = 0;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            if (line.Utf8Length > options.MaxEventBytes)
                throw new InvalidDataException("SSE event exceeds configured MaxEventBytes; output was not truncated.");
            if (result.Count >= options.MaxBufferedRoundFrames || line.Utf8Length > options.MaxBufferedRoundBytes - bytes)
                throw new InvalidDataException("SSE round exceeds configured buffer byte/frame budget; output was not truncated.");
            bytes += line.Utf8Length;
            result.Enqueue(line);
        }

        return result;
    }

    private static async IAsyncEnumerable<SseEvent> ValidateUpstreamCompletion(
        IAsyncEnumerable<SseFrame> lines,
        StreamResponseCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            capture.Accept(line);
            if (line.Event is { } decoded) yield return decoded;
        }

        if (!capture.IsComplete)
        {
            throw new UpstreamException("upstream stream ended before its terminal event");
        }
    }

    private static IAsyncEnumerable<SseFrame> ConvertRound(
        ProxyStreamContext context,
        IAsyncEnumerable<SseEvent> lines,
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
            (ProtocolConverter.Responses, ProtocolConverter.Chat) => SseStreamConverter.ChatToResponsesFrames(
                lines, model, converted, skipped, state?.HasResponse == true,
                state?.SequenceNumber ?? 0, state?.NextOutputIndex ?? 0, cancellationToken),
            (ProtocolConverter.Responses, ProtocolConverter.Messages) => SseStreamConverter.MessagesToResponsesFrames(
                lines, model, converted, skipped, state?.HasResponse == true,
                state?.SequenceNumber ?? 0, state?.NextOutputIndex ?? 0, cancellationToken),
            (ProtocolConverter.Messages, ProtocolConverter.Chat) => SseStreamConverter.ChatToMessagesFrames(
                lines, model, converted, null, false, cancellationToken),
            (ProtocolConverter.Chat, ProtocolConverter.Messages) => SseStreamConverter.MessagesToChatFrames(
                lines, model, converted, null, includeUsage, cancellationToken),
            (ProtocolConverter.Chat, ProtocolConverter.Responses) => SseStreamConverter.ResponsesToChatFrames(
                lines, model, converted, null, cancellationToken),
            (ProtocolConverter.Messages, ProtocolConverter.Responses) => SseStreamConverter.ResponsesToMessagesFrames(
                lines, model, converted, null, false, cancellationToken),
            _ => throw new BadRequestException($"streaming conversion not implemented for {context.EntryProtocol} to {context.ChannelType}")
        };
    }

    private sealed class ConvertedProxyStreamState
    {
        public required Dictionary<string, object?> UpstreamRequest { get; set; }
        public ProxyLogBodyHandle? UpstreamRequestBody { get; set; }
        public Dictionary<string, object?>? UpstreamResponse { get; set; }
        public Dictionary<string, object?>? ResponsePayload { get; set; }
        public Dictionary<string, object?> RejectedUsage { get; } = new(StringComparer.Ordinal);
    }
}
