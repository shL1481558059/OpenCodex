using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Services;

public sealed partial class MultiAgentResponseService
{
    public async Task<IActionResult> ResponsesWebSocket()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return new NotFoundResult();

        var identity = identityContext.RequireIdentity();
        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(Request, HttpContext.Connection.RemoteIpAddress?.ToString());
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        using var sendGate = new SemaphoreSlim(1, 1);
        var ct = stop.Token;
        var maximumBytes = ProxyRequestLimits.ResolveMaxRequestBodyBytes(configuration);
        Task active = Task.CompletedTask;
        CancellationTokenSource? activeStop = null;
        string activeResponseId = "";
        string terminalResponseId = "";
        Channel<MultiAgentInjection>? injections = null;
        MultiAgentRun? connectionRun = null;
        NativeClientSession? connectionBinding = null;
        string? connectionSession = null;
        using var knownIds=HttpContext.RequestServices.GetRequiredService<ClientConnectionCache>().Open(identity.ApiKeyId);
        var streamingLimits=StreamingOptions();
        if(streamingLimits.MaxPendingInjections<=0||streamingLimits.MaxPendingInjectionBytes<=0||streamingLimits.MaxPendingInjectionBytes>long.MaxValue/8)throw new InvalidOperationException("Invalid injection queue limits.");
        long pendingInjectionBytes=0;
        var sequence = 0;

        async Task Send(Dictionary<string, object?> item)
        {
            await sendGate.WaitAsync(ct);
            try
            {
                var type = JsonDictionaryValue.String(item, "type");
                var responseId = JsonDictionaryValue.Get(item, "response") is IReadOnlyDictionary<string, object?> response
                    ? JsonDictionaryValue.String(response, "id")
                    : "";
                if (type == "response.created" && responseId.Length > 0)
                {
                    activeResponseId = responseId;
                    knownIds.RememberId(responseId);
                }
                else if (type is "response.completed" or "response.failed" or "response.incomplete"
                    && responseId.Length > 0)
                {
                    knownIds.RememberId(responseId);
                    knownIds.RememberResponse(responseId,CompletedResponseMarker,true,true);
                    terminalResponseId = responseId;
                }
                if (item.TryGetValue("sequence_number", out var value))
                {
                    sequence = Math.Max(sequence + 1, WebSearchPayload.ToInt(value, sequence));
                    item["sequence_number"] = sequence;
                }
                await OpenCodex.Core.Protocols.WebSocketJsonWriter.SendAsync(socket,item,configuration.GetValue<int?>("ProxyStreaming:MaxEventBytes")??16*1024*1024,ct);
            }
            finally { sendGate.Release(); }
        }

        async Task ErrorAndClose(string message)
        {
            await Send(new Dictionary<string, object?>
            {
                ["type"] = "error", ["status"] = 400,
                ["error"] = new Dictionary<string, object?> { ["type"] = "invalid_request_error", ["message"] = message }
            });
            await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid request", ct);
            stop.Cancel();
        }

        async Task Execute(Dictionary<string, object?> request, MultiAgentRun? run, Channel<MultiAgentInjection> channel,
            bool generate, CancellationToken responseCt)
        {
            var binding = connectionBinding;
            var runtimeLease = binding is null ? await store.AcquireRequestAsync(run!,responseCt) : null;
            var responseId = "";

            try
            {
                if (binding is not null)
                {
                    // Upgrade headers identify a connection, not each subsequent response.create.
                    // Without a per-message turn identity, content equality cannot imply a retry.
                    var result = generate
                        ? await ExecuteClient(request, binding, metadata, Send, responseCt, requestScopedTurn: false)
                        : await ExecuteClientWarmup(request, binding, Send, responseCt);
                    responseId = JsonDictionaryValue.String(result, "id");
                }
                else
                {
                    var persist = JsonDictionaryValue.Get(request, "store") is not false;
                    var runtime = new MultiAgentRuntime(run!, (payload, onEvent, token) => CallModelAsync(payload, metadata, onEvent, token), Send,
                        () => store.SaveAsync(run!, CancellationToken.None, persist),streamingOptions:StreamingOptions());
                    await runtime.ExecuteAsync(request, responseCt, channel.Reader);
                }
            }
            catch (OperationCanceledException) when (responseCt.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Multi-agent WebSocket response failed for run {RunId}", run?.Id ?? binding?.ThreadId);
                await Send(new Dictionary<string, object?>
                {
                    ["type"] = "response.failed", ["sequence_number"] = 0,
                    ["response"] = new Dictionary<string, object?>
                    {
                        ["id"] = run?.LastResponseId ?? responseId, ["object"] = "response", ["status"] = "failed",
                        ["model"] = JsonDictionaryValue.String(request, "model"), ["output"] = new List<object?>(),
                        ["error"] = new Dictionary<string, object?>
                        {
                            ["code"] = exception is ProxyException proxy ? proxy.ErrorType : "server_error",
                            ["message"] = exception is BadRequestException ? exception.Message : "The multi-agent response failed. Check server logs before retrying."
                        }
                    }
                });
            }
            finally
            {
                if (run is not null) responseId = run.LastResponseId;
                if (responseId.Length > 0) knownIds.RememberId(responseId);
                channel.Writer.TryComplete();
                if(runtimeLease is not null)await runtimeLease.DisposeAsync();
                try
                {
                    while(channel.Reader.TryRead(out var pending))
                    { pending.Dispose();if(!ct.IsCancellationRequested)await InjectionFailed(pending.ResponseId,pending.Input); }
                }
                finally{while(channel.Reader.TryRead(out var abandoned))abandoned.Dispose();}
            }
        }

        Task InjectionFailed(string responseId, List<object?> input)
        {
            var completed = knownIds.FindResponse<object>(responseId) is not null || terminalResponseId==responseId;
            return Send(new Dictionary<string, object?>
            {
                ["type"] = "response.inject.failed", ["sequence_number"] = 0,
                ["response_id"] = responseId, ["input"] = input,
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = completed ? "response_already_completed" : "response_not_found",
                    ["message"] = completed ? "The response has already completed." : "The response was not found on this connection."
                }
            });
        }

        try
        {
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, ct);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                        return new EmptyResult();
                    }
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > maximumBytes)
                    {
                        await ErrorAndClose("Only text JSON messages within the configured request size limit are accepted.");
                        return new EmptyResult();
                    }
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);

                Dictionary<string, object?> item;
                try
                {
                    using var json = JsonDocument.Parse(message.ToArray());
                    item = WebSearchPayload.FromJsonElement(json.RootElement) as Dictionary<string, object?>
                        ?? throw new JsonException("A JSON object is required.");
                }
                catch (JsonException)
                {
                    await ErrorAndClose("The message must be a valid JSON object.");
                    return new EmptyResult();
                }

                var type = JsonDictionaryValue.String(item, "type");
                if (type == "response.create")
                {
                    if (!active.IsCompleted)
                    {
                        await Send(new Dictionary<string, object?>
                        {
                            ["type"] = "error", ["status"] = 400,
                            ["error"] = new Dictionary<string, object?>
                            {
                                ["type"] = "invalid_request_error", ["code"] = "response_in_progress",
                                ["message"] = "Only one response may be active on this connection. The current response is still running."
                            }
                        });
                        continue;
                    }
                    item.Remove("type");
                    var generate = JsonDictionaryValue.Get(item, "generate") is not false;
                    var model = JsonDictionaryValue.String(item, "model");
                    if (model.Length == 0)
                    {
                        await ErrorAndClose("model is required.");
                        return new EmptyResult();
                    }
                    var options = JsonDictionaryValue.Object(item, "multi_agent", WebSearchPayload.DeepCopyObject);
                    if (JsonDictionaryValue.Get(options, "enabled") is false)
                    {
                        var genericDisabled = HttpContext.RequestServices.GetRequiredService<ResponsesWebSocketClientService>();
                        item["type"] = "response.create";
                        await genericDisabled.HandleAsync(socket, item, metadata, ct);
                        return new EmptyResult();
                    }
                    var clientIdentity = MultiAgentClientIdentity.Parse(metadata.Headers);
                    var existingNative = clientIdentity is not null
                        && await NativeSessions.TryGetAsync(identity.ApiKeyId, clientIdentity.ThreadId, ct) is not null;
                    var simulatesMultiAgent = catalog.SimulatesMultiAgent(model);
                    if (!simulatesMultiAgent && !existingNative)
                    {
                        var generic = HttpContext.RequestServices.GetRequiredService<ResponsesWebSocketClientService>();
                        item["type"] = "response.create";
                        await generic.HandleAsync(socket, item, metadata, ct);
                        return new EmptyResult();
                    }
                    connectionSession ??= SessionId(item);
                    try
                    {
                        connectionBinding = await ResolveClientBinding(item, clientIdentity, simulatesMultiAgent, ct);
                        if (connectionBinding is not null)
                            connectionRun = null;
                        else
                        {
                            if (!simulatesMultiAgent)
                            {
                                await ErrorAndClose("v2 Agent simulation is not enabled for this model in the model catalog.");
                                return new EmptyResult();
                            }
                            connectionRun = await store.GetAsync($"{identity.ApiKeyId:N}:{connectionSession}",
                            JsonDictionaryValue.String(item, "previous_response_id"), () => new MultiAgentRun
                            {
                                Model = model, Template = MultiAgentProtocol.NormalizeRequest(item),
                                MaxConcurrentSubagents = ConcurrentLimit(options),
                                CompactThresholdTokens = CompactThreshold(item),
                                Agents = new Dictionary<string, MultiAgentState>(StringComparer.Ordinal)
                                {
                                    ["/root"] = new() { Name = "/root", History = MultiAgentProtocol.InitialHistory(item) }
                                }
                            }, ct, identity.ApiKeyId);
                        }
                        if (connectionRun is not null && connectionRun.Model != model)
                            throw new BadRequestException("Start a new session to change models.");
                        if (!generate)
                        {
                            if (connectionBinding is null)
                                throw new BadRequestException("Native prewarm requires a client thread identity.");
                            item.Remove("generate");
                        }
                    }
                    catch (Exception exception) when (exception is BadRequestException or KeyNotFoundException)
                    {
                        await ErrorAndClose(exception.Message);
                        return new EmptyResult();
                    }
                    injections = Channel.CreateBounded<MultiAgentInjection>(new BoundedChannelOptions(streamingLimits.MaxPendingInjections)
                    {
                        SingleReader = true, SingleWriter = true, FullMode=BoundedChannelFullMode.Wait
                    });
                    activeStop?.Dispose();
                    activeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    activeResponseId = "";
                    terminalResponseId = "";
                    active = Execute(item, connectionRun, injections, generate, activeStop.Token);
                }
                else if (type == "response.interrupt")
                {
                    var responseId = JsonDictionaryValue.String(item, "response_id");
                    var mode = JsonDictionaryValue.String(item, "mode");
                    if (responseId.Length == 0 || mode is not ("" or "discard_partial_items"))
                    {
                        await Send(new Dictionary<string, object?>
                        {
                            ["type"] = "response.interrupt.failed",
                            ["response_id"] = responseId,
                            ["error"] = new Dictionary<string, object?>
                            {
                                ["code"] = "invalid_request",
                                ["message"] = "response.interrupt requires response_id and mode discard_partial_items."
                            }
                        });
                        continue;
                    }

                    if (!active.IsCompleted && activeResponseId == responseId
                        && terminalResponseId != responseId && activeStop is not null)
                    {
                        activeStop.Cancel();
                        await active;
                    }

                    if (knownIds.Knows(responseId)||activeResponseId==responseId||terminalResponseId==responseId)
                    {
                        await Send(new Dictionary<string, object?>
                        {
                            ["type"] = "response.interrupt.accepted",
                            ["response_id"] = responseId
                        });
                    }
                    else
                    {
                        await Send(new Dictionary<string, object?>
                        {
                            ["type"] = "response.interrupt.failed",
                            ["response_id"] = responseId,
                            ["error"] = new Dictionary<string, object?>
                            {
                                ["code"] = "response_not_found",
                                ["message"] = "The response was not found on this connection."
                            }
                        });
                    }
                }
                else if (type == "response.inject")
                {
                    if (connectionBinding is not null)
                    {
                        await ErrorAndClose("Native clients return tool results in the next response.create. response.inject is supported only for legacy server agents.");
                        return new EmptyResult();
                    }
                    var responseId = JsonDictionaryValue.String(item, "response_id");
                    if (responseId.Length == 0 || JsonDictionaryValue.Get(item, "input") is not List<object?> input
                        || input.Count == 0 || input.Any(value => value is not IReadOnlyDictionary<string, object?> output
                            || JsonDictionaryValue.String(output, "type") is not ("function_call_output" or "custom_tool_call_output")
                            || JsonDictionaryValue.String(output, "call_id").Length == 0 || !output.ContainsKey("output")))
                    {
                        await ErrorAndClose("response.inject requires response_id and a non-empty input array of objects.");
                        return new EmptyResult();
                    }
                    if(!active.IsCompleted&&injections is not null)
                    {
                        var weight=InjectionWeight(input,streamingLimits.MaxPendingInjectionBytes);
                        var charged=Interlocked.Add(ref pendingInjectionBytes,weight);
                        if(charged<=streamingLimits.MaxPendingInjectionBytes)
                        {
                            var queued=new MultiAgentInjection{ResponseId=responseId,Input=input,QueueLease=new InjectionLease(()=>Interlocked.Add(ref pendingInjectionBytes,-weight))};
                            if(injections.Writer.TryWrite(queued))continue;
                            queued.Dispose();
                        }
                        else Interlocked.Add(ref pendingInjectionBytes,-weight);
                        await Send(new(){["type"]="response.inject.failed",["response_id"]=responseId,["input"]=input,
                            ["error"]=new Dictionary<string,object?>{["code"]="queue_limit_exceeded",["message"]="The injection queue admission limit was reached; the input was not accepted."}});
                        continue;
                    }
                    await InjectionFailed(responseId,input);
                }
                else
                {
                    await ErrorAndClose("Supported event types are response.create, response.inject, and response.interrupt.");
                    return new EmptyResult();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Multi-agent WebSocket disconnected");
        }
        finally
        {
            stop.Cancel();
            injections?.Writer.TryComplete();
            try { await active; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException) { }
            activeStop?.Dispose();
        }
        return new EmptyResult();
    }
    private static readonly object CompletedResponseMarker=new();
    private sealed class InjectionLease(Action release):IDisposable
    {private Action? _release=release;public void Dispose()=>Interlocked.Exchange(ref _release,null)?.Invoke();}
    private static long InjectionWeight(object? value,long cap,int depth=0)
    {
        if(depth>64)return cap+1;
        if(value is string text)return 24L+2L*text.Length;
        long total=64;
        if(value is IReadOnlyDictionary<string,object?> map)
        {foreach(var pair in map){total+=64+2L*pair.Key.Length+InjectionWeight(pair.Value,cap,depth+1);if(total>cap)return cap+1;}}
        else if(value is IReadOnlyList<object?> items)
        {foreach(var item in items){total+=8+InjectionWeight(item,cap,depth+1);if(total>cap)return cap+1;}}
        return total;
    }

}
