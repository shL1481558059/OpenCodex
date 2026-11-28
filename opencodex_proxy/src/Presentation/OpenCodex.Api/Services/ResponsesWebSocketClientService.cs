using System.Net.WebSockets;
using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

/// <summary>Runs client-owned Responses requests over one WebSocket connection.</summary>
public sealed class ResponsesWebSocketClientService(
    IServiceScopeFactory scopes,
    ILogger<ResponsesWebSocketClientService> logger,
    ClientConnectionCache connectionCache,
    IProxyIdentityContext identityContext)
{
    private const string WarmResponsePrefix = "resp_ocx_warm_";

    public async Task HandleAsync(
        WebSocket socket,
        Dictionary<string, object?> firstRequest,
        ProxyRequestMetadata metadata,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        using var history = connectionCache.Open(identityContext.RequireIdentity().ApiKeyId);
        var ct = stop.Token;
        Task active = Task.CompletedTask;
        CancellationTokenSource? activeStop = null;
        string activeResponseId = "";
        string activeModel = "";
        string terminalResponseId = "";
        var modelErrorTerminal = false;
        var maximumBytes = ProtocolConversionParameters.Current.MaxRequestBodyBytes;
        PreparedClientRequest? activePrepared = null;
        var first = firstRequest;

        async Task Send(Dictionary<string, object?> payload, CancellationToken? sendCancellation = null, bool recordIdentity = true)
        {
            var sendToken = sendCancellation ?? ct;
            await sendGate.WaitAsync(sendToken);
            try
            {
                if (socket.State != WebSocketState.Open) throw new WebSocketException(WebSocketError.InvalidState);
                ValidateWire(payload, sendToken);
                var type = JsonDictionaryValue.String(payload, "type");
                if (type is "response.completed" or "response.failed" or "response.incomplete" && terminalResponseId.Length > 0) return;
                var responseId = JsonDictionaryValue.Get(payload, "response") is IReadOnlyDictionary<string, object?> response
                    ? JsonDictionaryValue.String(response, "id")
                    : "";
                if (type == "response.created" && responseId.Length > 0)
                {
                    activeResponseId = responseId;
                    if (recordIdentity && !history.Knows(responseId))
                        history.RememberId(responseId, (activePrepared ?? throw new InvalidOperationException("Response identity has no request reservation.")).Reservation, sendToken);
                }
                else if (type is "response.completed" or "response.failed" or "response.incomplete"
                    && responseId.Length > 0)
                {
                    if (recordIdentity && !history.Knows(responseId))
                        history.RememberId(responseId, (activePrepared ?? throw new InvalidOperationException("Response identity has no request reservation.")).Reservation, sendToken);
                    terminalResponseId = responseId;
                }
                await OpenCodex.ProtocolConversion.Streaming.WebSocketJsonWriter.SendAsync(socket,payload,ProtocolConversionParameters.Current.SseMaxEventBytes,sendToken);
            }
            finally { sendGate.Release(); }
        }

        async Task SendError(string code, string message, int status = 400)
        {
            await Send(new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["status"] = status,
                ["error"] = new Dictionary<string, object?>
                {
                    ["type"] = "invalid_request_error",
                    ["code"] = code,
                    ["message"] = message
                }
            });
        }

        void ValidateWire(Dictionary<string, object?> payload, CancellationToken token)
        {
            if (ProtocolJsonWriter.Count(payload, audit: false, token) > ProtocolConversionParameters.Current.SseMaxEventBytes)
                throw new InvalidDataException("A generated WebSocket message exceeds configured MaxEventBytes.");
        }

        async Task Execute(Dictionary<string, object?> request, bool generate, int createIndex,
            CancellationToken responseCt)
        {
            var model = JsonDictionaryValue.String(request, "model");
            activeModel = model;
            // Request-scoped: a local failure terminal never suppresses later request errors.
            modelErrorTerminal = false;
            try
            {
                var previousId = JsonDictionaryValue.String(request, "previous_response_id");
                using var previous = previousId.Length == 0 ? null : history.AcquireHistory(previousId);
                if (previousId.Length > 0 && previous is null)
                {
                    await SendError("previous_response_not_found",
                        "The previous response is unavailable on this WebSocket connection. Resend the full client history without previous_response_id.");
                    return;
                }
                using var prepared = history.Prepare(request, previous, generate, responseCt);
                activePrepared = prepared;
                var fullRequest = prepared.Request;
                logger.LogInformation(
                    "Responses WebSocket create projected. create_index={CreateIndex} model={Model} generate={Generate} transport={Transport} previous_response_id={PreviousResponseId} client_input_item_count={ClientInputItemCount} reused_history_item_count={ReusedHistoryItemCount} projected_input_item_count={ProjectedInputItemCount}",
                    createIndex, model, generate, "websocket", previousId, InputItemCount(request),
                    previous is null ? 0 : previous.InputCount + previous.OutputCount,
                    JsonDictionaryValue.List(fullRequest, "input").Count);

                if (!generate)
                {
                    var responseId = WarmResponsePrefix + Guid.NewGuid().ToString("N");
                    var response = SyntheticResponse(responseId, model, "completed");
                    var created = new Dictionary<string, object?>
                    {
                        ["type"] = "response.created", ["response"] = SyntheticResponse(responseId, model, "in_progress")
                    };
                    var completed = new Dictionary<string, object?> { ["type"] = "response.completed", ["response"] = response };
                    ValidateWire(created, responseCt);
                    ValidateWire(completed, responseCt);
                    prepared.ReserveKnownResponse(response, responseCt);
                    prepared.Commit(response, responseCt);
                    await Send(created, responseCt);
                    await Send(completed, responseCt);
                    return;
                }

                MultiAgentModelStreamWriter writer = null!;
                writer = new MultiAgentModelStreamWriter(async (payload, token) =>
                {
                    if (terminalResponseId.Length > 0 || modelErrorTerminal) return;
                    var type = JsonDictionaryValue.String(payload, "type");
                    if (type == "error")
                    {
                        // The Core terminal gate already emitted the single local failure. A later
                        // exception unwind must not turn it into a second response.failed terminal.
                        modelErrorTerminal = true;
                        await Send(payload, token);
                        return;
                    }
                    if (type is "response.completed" or "response.incomplete")
                    {
                        ValidateWire(payload, token);
                        var response = writer.Result ?? throw new InvalidOperationException("The terminal model event has no validated response.");
                        activeResponseId = JsonDictionaryValue.String(response, "id");
                        prepared.Commit(response, token);
                    }
                    await Send(payload, token);
                });
                await using var scope = scopes.CreateAsyncScope();
                var endpoint = scope.ServiceProvider.GetRequiredService<IProxyEndpointService>();
                var result = await endpoint.ProxyAsync(new ProxyEndpointContext(
                    ProtocolConverter.Responses, fullRequest, metadata, writer, responseCt, request)
                {
                    SnapshotReservation = prepared.Reservation,
                });
                if (!result.IsEmpty)
                {
                    await SendProxyError(result);
                    return;
                }
                if (writer.Result is null || JsonDictionaryValue.String(writer.Result, "id").Length == 0)
                    await SendError("upstream_error", "The upstream response completed without a response object.", 502);
            }
            catch (OperationCanceledException) when (responseCt.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Responses WebSocket request failed for model {Model}", model);
                if (terminalResponseId.Length > 0 || modelErrorTerminal) return;
                var status = exception switch
                {
                    SnapshotPersistenceException => 507,
                    OpenCodex.ProtocolConversion.Protocols.RequestBudgetExceededException => 413,
                    OpenCodex.Core.Errors.ProxyException proxy => proxy.StatusCode,
                    _ => 502
                };
                var code = exception switch
                {
                    SnapshotPersistenceException => "snapshot_write_failed",
                    OpenCodex.Core.Errors.StorageCapacityException storage => storage.ErrorCode,
                    OpenCodex.ProtocolConversion.Protocols.RequestBudgetExceededException => "request_too_large",
                    InvalidDataException => "output_event_limit",
                    OpenCodex.Core.Errors.ProxyException proxy when proxy.StatusCode == 413 => "request_too_large",
                    OpenCodex.Core.Errors.BadRequestException => "invalid_request",
                    OpenCodex.Core.Errors.UpstreamException upstream when upstream.ErrorCode is { Length: > 0 } => upstream.ErrorCode,
                    OpenCodex.Core.Errors.ProxyException proxy => proxy.ErrorType,
                    _ => "server_error"
                };
                var message = exception is SnapshotPersistenceException
                    or OpenCodex.Core.Errors.ProxyException or OpenCodex.ProtocolConversion.Protocols.RequestBudgetExceededException
                    ? exception.Message : "The upstream request failed.";
                if (activeResponseId.Length > 0 && history.Knows(activeResponseId))
                {
                    var failed = SyntheticResponse(activeResponseId, activeModel, "failed");
                    failed["error"] = new Dictionary<string, object?>
                    {
                        ["type"] = status == 507 ? "insufficient_storage" : status == 413 ? "invalid_request_error" : "upstream_error",
                        ["code"] = code, ["status"] = status, ["message"] = message
                    };
                    await Send(new Dictionary<string, object?> { ["type"] = "response.failed", ["response"] = failed }, recordIdentity: false);
                }
                else await SendError(code, message, status);
            }
            finally { activePrepared = null; }
        }

        async Task SendProxyError(ProxyEndpointResult result)
        {
            var error = result.Payload is IReadOnlyDictionary<string, object?> payload
                && JsonDictionaryValue.Get(payload, "error") is IReadOnlyDictionary<string, object?> errorPayload
                    ? errorPayload
                    : new Dictionary<string, object?>
                    {
                        ["type"] = "server_error",
                        ["message"] = "The request failed before a response stream started."
                    };
            await Send(new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["status"] = result.StatusCode,
                ["error"] = WebSearchPayload.DeepCopyObject(error)
            });
        }

        async Task<Dictionary<string, object?>?> ReceiveAsync()
        {
            try { return await WebSocketRequestReader.ReadAsync(socket, maximumBytes, ct); }
            catch (OpenCodex.ProtocolConversion.Protocols.RequestBudgetExceededException exception)
            {
                await SendError("request_too_large", exception.Message, 413);
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Request too large", ct);
                return null;
            }
            catch (JsonException)
            {
                await SendError("invalid_payload", "The message must be a valid text JSON object.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid request", ct);
                return null;
            }
        }

        async Task HandleInterrupt(Dictionary<string, object?> interrupt)
        {
            var responseId = JsonDictionaryValue.String(interrupt, "response_id");
            var mode = JsonDictionaryValue.String(interrupt, "mode");
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
                return;
            }

            var interrupted = false;
            if (!active.IsCompleted && activeResponseId == responseId
                && terminalResponseId != responseId && activeStop is not null)
            {
                activeStop.Cancel();
                await active;
                interrupted = terminalResponseId != responseId;
                if (interrupted) history.DiscardHistory(responseId);
            }

            if (history.Knows(responseId) || activeResponseId == responseId || terminalResponseId == responseId)
            {
                await Send(new Dictionary<string, object?>
                {
                    ["type"] = "response.interrupt.accepted",
                    ["response_id"] = responseId
                });
                if (interrupted)
                {
                    await Send(new Dictionary<string, object?>
                    {
                        ["type"] = "response.incomplete",
                        ["response"] = InterruptedResponse(responseId, activeModel)
                    });
                }
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

        async Task<Dictionary<string, object?>?> RunActiveAsync()
        {
            var inbound = ReceiveAsync();
            while (true)
            {
                if (await Task.WhenAny(active, inbound) == active)
                {
                    await active;
                    return await inbound;
                }

                var received = await inbound;
                if (received is null)
                {
                    activeStop?.Cancel();
                    await active;
                    return null;
                }

                var type = JsonDictionaryValue.String(received, "type");
                if (type == "response.interrupt")
                {
                    await HandleInterrupt(received);
                    if (active.IsCompleted) return await ReceiveAsync();
                    inbound = ReceiveAsync();
                    continue;
                }
                if (type == "response.create")
                {
                    // A local failure terminal (model error event) also ends this request;
                    // a following create must be unwound rather than reported in-progress.
                    if (terminalResponseId.Length > 0 || modelErrorTerminal || active.IsCompleted)
                    {
                        await active;
                        return received;
                    }
                    await SendError("response_in_progress", "Only one response may be active on this connection.");
                    inbound = ReceiveAsync();
                    continue;
                }

                await SendError(
                    "unsupported_event",
                    "Supported event types are response.create and response.interrupt.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Unsupported event", ct);
                activeStop?.Cancel();
                await active;
                return null;
            }
        }

        try
        {
            Dictionary<string, object?>? item = first;
            var createIndex = 0;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested && item is not null)
            {
                var type = JsonDictionaryValue.String(item, "type");
                if (type == "response.interrupt")
                {
                    await HandleInterrupt(item);
                    item = await ReceiveAsync();
                    continue;
                }
                if (type != "response.create")
                {
                    await SendError(
                        "unsupported_event",
                        "Supported event types are response.create and response.interrupt.");
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Unsupported event", ct);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(JsonDictionaryValue.String(item, "stream_id")))
                {
                    await SendError("unsupported_stream_id", "Named stream_id lanes are not supported by this endpoint.");
                    return;
                }
                item.Remove("type");
                item.Remove("stream_id");
                var generate = JsonDictionaryValue.Get(item, "generate") is not false;
                item.Remove("generate");
                if (JsonDictionaryValue.String(item, "model").Length == 0)
                {
                    await SendError("invalid_request_error", "model is required.");
                    return;
                }
                activeStop?.Dispose();
                activeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                activeResponseId = "";
                activeModel = "";
                terminalResponseId = "";
                modelErrorTerminal = false;
                active = Execute(item, generate, ++createIndex, activeStop.Token);
                item = await RunActiveAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "Responses WebSocket client disconnected");
        }
        finally
        {
            stop.Cancel();
            activeStop?.Cancel();
            try { await active; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException) { }
            activeStop?.Dispose();
        }
    }

    private static int InputItemCount(IReadOnlyDictionary<string, object?> request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        return input is string ? 1 : JsonDictionaryValue.List(request, "input").Count;
    }

    private static Dictionary<string, object?> SyntheticResponse(
        string id,
        string model,
        string status) => new(StringComparer.Ordinal)
    {
        ["id"] = id,
        ["object"] = "response",
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["status"] = status,
        ["model"] = model,
        ["output"] = new List<object?>(),
        ["store"] = false
    };

    private static Dictionary<string, object?> InterruptedResponse(
        string id,
        string model)
    {
        var response = SyntheticResponse(id, model, "incomplete");
        response["incomplete_details"] = new Dictionary<string, object?>
        {
            ["reason"] = "interrupted"
        };
        return response;
    }

}
