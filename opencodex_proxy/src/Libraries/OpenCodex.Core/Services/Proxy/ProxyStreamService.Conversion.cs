using System.Runtime.CompilerServices;
using OpenCodex.Core.Errors;
using OpenCodex.Core.ExternalIntegrations;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Abstractions;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Core.Services.Proxy;

public sealed partial class ProxyStreamService
{
    private async IAsyncEnumerable<SseFrame> ConvertRoundsAsync(
        ProxyStreamContext context,
        ConvertedProxyStreamState state,
        BuiltinToolSession? tools,
        RequestExecutionBudget executionBudget,
        ProxyRequestMetadata requestMetadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamState = tools is null ? null : new WebSearchStreamEventState();
        var skipped = tools is null ? null : new HashSet<string>([tools.Binding.WebSearchToolName], StringComparer.Ordinal);
        var textFormat = ProtocolConverter.ExtractTextFormat(context.OriginalPayload);
        var mappings = context.EntryProtocol == ProtocolConverter.Responses
            && context.ChannelType is ProtocolConverter.Chat or ProtocolConverter.Messages
            ? context.ToolCallMappings ?? ProtocolConverter.BuildResponsesToolCallMappings(context.Payload)
            : null;
        var committed = false;
        var retryUsed = false;

        while (true)
        {
            var token = tools?.Token(cancellationToken) ?? cancellationToken;
            executionBudget.EnsureCall(requestMetadata, context.Payload, state.UpstreamRequest, _outputFrames, context.ChannelType,
                isStream: true, token, knownAuditDetails: tools?.Details, hasBuiltinTools: tools is not null);
            state.UpstreamRequestBody ??= executionBudget.CaptureAuditJson(state.UpstreamRequest, token);
            state.FailureEvidence = null;
            var knownNames=new HashSet<string>(skipped ?? (IEnumerable<string>)Array.Empty<string>(),StringComparer.Ordinal);
            if(mappings is not null)
            {
                knownNames.UnionWith(mappings.Keys);
                foreach(var mapping in mappings.Values)
                    if(ProtocolConverter.TryGetResponsesToolCallMapping(mapping.ResponsesName,mappings,out _))knownNames.Add(mapping.ResponsesName);
            }
            using var capture = CreateFullCapture(context.ChannelType, executionBudget, token, knownNames);
            var completionAttempted = false;
            StreamResponseCaptureResult? completion = null;
            Exception? roundFailure = null;
            ReceivedFrameEvidenceSnapshot? evidence = null;
            Dictionary<string, object?> Evidence(StreamCaptureTermination termination = StreamCaptureTermination.UpstreamError)
            {
                evidence ??= executionBudget.Retain(capture.GetFailureEvidence(termination));
                return evidence.Response;
            }
            StreamResponseCaptureResult CompleteOnce(StreamCaptureTermination termination)
            {
                if (completion is not null) return completion;
                if (completionAttempted) throw new InvalidOperationException("Source completion was already attempted; use received-frame failure evidence.");
                completionAttempted = true;
                return completion = CompleteCapture(capture, termination);
            }
            var converted = new ConvertedStreamResult { TextFormat = textFormat, ToolCallMappings = mappings,SharedCapture=capture };
            var upstreamLines = _upstream.StreamEventsAsync(context.Route.Channel, state.UpstreamRequest, context.DefaultTimeout, token);
            IAsyncEnumerable<SseFrame> confirmed;
            try
            {
                confirmed = await UpstreamStreamPrimer.PrimeAsync(
                    RecordReceivedFrames(upstreamLines, capture, token), token);
            }
            catch (Exception failure)
            {
                state.FailureEvidence = failure is UpstreamException { Body: Dictionary<string, object?> body }
                    && UpstreamJsonEvidence.IsEvidence(body) ? body : Evidence();
                state.UpstreamResponse = state.FailureEvidence;
                throw ResourceErrors.Normalize(failure);
            }
            var validated = ValidateUpstreamCompletion(confirmed, capture, token);
            var lines = ConvertRound(context, validated, converted, streamState, skipped, token);
            SseFrame? terminal = null;
            var retryRound=false;
            await using(var enumerator=lines.GetAsyncEnumerator(token))
            {
                try
                {
                    while(true)
                    {
                        bool moved;
                        try { moved=await enumerator.MoveNextAsync(); }
                        catch(UpstreamException exception) when(ToolContractRetryPolicy.IsUndeclaredToolCall(exception))
                        {
                            roundFailure = exception;
                            var rejected = converted.UpstreamResponse ?? Evidence();
                            if (converted.UpstreamResponse is not null)
                            {
                                try { RequestExecutionBudget.ValidateCompleteJson(rejected, "source", _outputFrames.MaxCaptureBytes, token); }
                                catch (Exception capacity)
                                {
                                    roundFailure = ResourceErrors.Normalize(capacity);
                                    state.FailureEvidence = Evidence();
                                    throw roundFailure;
                                }
                            }
                            if(tools is not null)tools.RecordRejectedRound(rejected,exception.Message);
                            else BuiltinToolSession.AddUsage(state.RejectedUsage,rejected is null?new(StringComparer.Ordinal):ObjectValue(rejected,"usage"));
                            // Semantic commitment survives every internal round. Never replay a role,
                            // response ID, text or tool start already handed to the downstream writer.
                            if(committed || tools?.HasCalls==true)throw;
                            if(retryUsed)throw ToolContractRetryPolicy.RetryExhausted(exception);
                            retryUsed=true;retryRound=true;
                            state.UpstreamRequest=ToolContractRetryPolicy.BuildRetryRequest(state.UpstreamRequest,
                                context.ChannelType,mappings?.Keys??Array.Empty<string>(),tools?.Binding.WebSearchToolName);
                            state.UpstreamRequestBody = null; // Capture only after the next P/quota admission.
                            break;
                        }
                        catch (Exception exception)
                        {
                            roundFailure = ResourceErrors.Normalize(exception);
                            throw roundFailure;
                        }
                        if(!moved)break;
                        var frame=enumerator.Current;
                        if(tools is not null && WebSearchStreamEventState.IsTerminal(frame))terminal=frame;
                        else
                        {
                            var visible=tools is null?frame:streamState!.Observe(frame,tools.Results);
                            if(visible.IsSemantic)committed=true; // before yield, even if write fails partially
                            yield return visible;
                        }
                    }
                }
                finally
                {
                    state.UpstreamResponse = converted.UpstreamResponse ?? completion?.Response;
                    if (state.UpstreamResponse is null && roundFailure is not null && !completionAttempted
                        && !capture.NeedsFailureEvidence && (roundFailure is IOException or OperationCanceledException
                            || roundFailure is UpstreamException { ErrorCode: null }))
                    {
                        try
                        {
                            state.UpstreamResponse = CompleteOnce(StreamCaptureTermination.UpstreamError).Response;
                            RequestExecutionBudget.ValidateCompleteJson(state.UpstreamResponse, "source",
                                _outputFrames.MaxCaptureBytes, CancellationToken.None);
                        }
                        catch (Exception captureFailure)
                        {
                            state.FailureEvidence = Evidence();
                            state.UpstreamResponse = state.FailureEvidence;
                            if (ResourceErrors.IsResourceFailure(captureFailure))
                                throw ResourceErrors.Combine(roundFailure, captureFailure);
                        }
                    }
                    if (roundFailure is not null || !capture.IsComplete)
                    {
                        // Never rebuild an already rejected/unfinished converter source; raw
                        // argument prefixes, including invalid partial JSON, remain in evidence.
                        if (roundFailure is not null && ResourceErrors.IsResourceFailure(roundFailure)
                            || state.UpstreamResponse is null)
                        {
                            state.FailureEvidence = Evidence();
                            state.UpstreamResponse = state.FailureEvidence;
                        }
                    }
                    else
                    {
                        try
                        {
                            state.UpstreamResponse ??= CompleteOnce(StreamCaptureTermination.Completed).Response;
                            RequestExecutionBudget.ValidateCompleteJson(state.UpstreamResponse, "source", _outputFrames.MaxCaptureBytes, token);
                        }
                        catch (Exception failure)
                        {
                            state.FailureEvidence = Evidence();
                            state.UpstreamResponse = state.FailureEvidence;
                            throw ResourceErrors.Normalize(failure);
                        }
                    }
                    state.CurrentEvidence = Evidence();
                }
            }
            if(retryRound)continue;

            if (converted.UpstreamResponse is null)
            {
                yield break;
            }
            var response = ProtocolConversion.ProtocolConversionErrors.Wrap(
                () => ProtocolConverter.ConvertOwnedResponse(
                converted.UpstreamResponse, context.EntryProtocol, context.ChannelType,
                context.Route.OriginalModel, textFormat, mappings));
            state.ResponsePayload = response;
            try { RequestExecutionBudget.ValidateCompleteJson(response, "downstream", _outputFrames.MaxCaptureBytes, token); }
            catch (Exception failure)
            {
                state.FailureEvidence = Evidence();
                state.ResponsePayload = null; // Rejected generated output was never a normal C-bounded response.
                throw ResourceErrors.Normalize(failure);
            }
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

            state.ToolExecutionPhase = true;
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
            state.ToolExecutionPhase = false;
            state.ResponsePayload = tools.ResponsePayload;
            if (tools.NextRequest is { } next)
            {
                state.UpstreamRequest = next;
                state.UpstreamRequestBody = null; // Every generated request is admitted before capture/model use.
                continue;
            }
            if (terminal is not null)
            {
                var final = streamState!.Complete(
                    terminal, tools.HasCalls ? tools.Usage : null, tools.ResponsePayload, out var finalResponse);
                state.ResponsePayload = finalResponse;
                try { RequestExecutionBudget.ValidateCompleteJson(finalResponse, "downstream", _outputFrames.MaxCaptureBytes, token); }
                catch (Exception failure)
                {
                    state.FailureEvidence = Evidence();
                    state.ResponsePayload = null; // Persist the failed response, not an over-C success candidate.
                    throw ResourceErrors.Normalize(failure);
                }
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

    private static async IAsyncEnumerable<SseFrame> RecordReceivedFrames(
        IAsyncEnumerable<SseFrame> frames, StreamResponseCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            ProtocolConversion.ProtocolConversionErrors.Wrap(() => capture.RecordReceivedFrame(frame));
            yield return frame;
        }
    }

    private static async IAsyncEnumerable<SseEvent> ValidateUpstreamCompletion(
        IAsyncEnumerable<SseFrame> lines,
        StreamResponseCapture capture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            ProtocolConversion.ProtocolConversionErrors.Wrap(() => capture.Accept(line, evidenceAlreadyRecorded: true));
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
        var frames = (context.EntryProtocol, context.ChannelType) switch
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
        return ProtocolConversion.ProtocolConversionErrors.WrapStream(frames, cancellationToken);
    }

    private sealed class ConvertedProxyStreamState
    {
        public required Dictionary<string, object?> UpstreamRequest { get; set; }
        public ProxyLogBodyHandle? UpstreamRequestBody { get; set; }
        public Dictionary<string, object?>? UpstreamResponse { get; set; }
        public Dictionary<string, object?>? ResponsePayload { get; set; }
        public Dictionary<string, object?>? FailureEvidence { get; set; }
        public Dictionary<string, object?>? CurrentEvidence { get; set; }
        public bool ToolExecutionPhase { get; set; }
        public Dictionary<string, object?> RejectedUsage { get; } = new(StringComparer.Ordinal);
    }
}
