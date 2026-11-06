using System.Net.WebSockets;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

/// <summary>Runs client-owned Responses requests over one WebSocket connection.</summary>
public sealed class ResponsesWebSocketClientService(
    IServiceScopeFactory scopes,
    ILogger<ResponsesWebSocketClientService> logger)
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
        var records = new Dictionary<string, StoredClientResponse>(StringComparer.Ordinal);
        var ct = stop.Token;
        Task active = Task.CompletedTask;
        var maximumBytes = 16 * 1024 * 1024;
        var first = firstRequest;

        async Task Send(Dictionary<string, object?> payload)
        {
            await sendGate.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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

        async Task Execute(Dictionary<string, object?> request, bool generate, int createIndex)
        {
            var model = JsonDictionaryValue.String(request, "model");
            // The parsed frame is request-scoped and immutable after projection.
            // Rewrite stages copy the payload before changing request semantics.
            var clientRequest = request;
            var fullRequest = ProjectRequest(request, records, out var previous);
            if (fullRequest is null)
            {
                await SendError(
                    "previous_response_not_found",
                    "The previous response is unavailable on this WebSocket connection. Resend the full client history without previous_response_id.");
                return;
            }

            var clientInputItems = InputItemCount(request);
            var projectedInputItems = JsonDictionaryValue.List(fullRequest, "input").Count;
            var reusedHistoryItems = previous is null
                ? 0
                : JsonDictionaryValue.List(previous.Request, "input").Count
                    + JsonDictionaryValue.List(previous.Response, "output").Count;
            logger.LogInformation(
                "Responses WebSocket create projected. create_index={CreateIndex} model={Model} generate={Generate} transport={Transport} previous_response_id={PreviousResponseId} client_input_item_count={ClientInputItemCount} reused_history_item_count={ReusedHistoryItemCount} projected_input_item_count={ProjectedInputItemCount}",
                createIndex,
                model,
                generate,
                "websocket",
                JsonDictionaryValue.String(request, "previous_response_id"),
                clientInputItems,
                reusedHistoryItems,
                projectedInputItems);

            if (!generate)
            {
                var responseId = WarmResponsePrefix + Guid.NewGuid().ToString("N");
                var response = SyntheticResponse(responseId, model, "completed");
                records[responseId] = new StoredClientResponse
                {
                    Request = fullRequest,
                    Response = response
                };
                await Send(new Dictionary<string, object?>
                {
                    ["type"] = "response.created",
                    ["response"] = SyntheticResponse(responseId, model, "in_progress")
                });
                await Send(new Dictionary<string, object?>
                {
                    ["type"] = "response.completed",
                    ["response"] = response
                });
                return;
            }

            fullRequest["stream"] = true;
            var writer = new MultiAgentModelStreamWriter(
                (payload, token) => Send(payload));
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var endpoint = scope.ServiceProvider.GetRequiredService<IProxyEndpointService>();
                var result = await endpoint.ProxyAsync(new ProxyEndpointContext(
                    ProtocolConverter.Responses,
                    fullRequest,
                    metadata,
                    writer,
                    ct,
                    clientRequest));
                if (!result.IsEmpty)
                {
                    await SendProxyError(result);
                    return;
                }

                var response = writer.Result;
                var responseId = response is null ? "" : JsonDictionaryValue.String(response, "id");
                if (response is null || responseId.Length == 0)
                {
                    await SendError("upstream_error", "The upstream response completed without a response object.", 502);
                    return;
                }

                if (JsonDictionaryValue.String(response, "status") is "completed" or "incomplete")
                {
                    records[responseId] = new StoredClientResponse
                    {
                        Request = fullRequest,
                        Response = response
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Responses WebSocket request failed for model {Model}", model);
                await SendError(
                    exception is OpenCodex.Core.Errors.ProxyException proxy ? proxy.ErrorType : "server_error",
                    exception is OpenCodex.Core.Errors.BadRequestException
                        ? exception.Message
                        : "The upstream request failed.",
                    exception is OpenCodex.Core.Errors.ProxyException failedProxy
                        ? failedProxy.StatusCode
                        : 502);
            }
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
            using var message = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                    return null;
                }
                if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > maximumBytes)
                {
                    await SendError("invalid_payload", "Only text JSON messages within the request limit are accepted.");
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid payload", ct);
                    return null;
                }
                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            try
            {
                using var json = JsonDocument.Parse(message.ToArray());
                return WebSearchPayload.FromJsonElement(json.RootElement) as Dictionary<string, object?>
                    ?? throw new JsonException("A JSON object is required.");
            }
            catch (JsonException)
            {
                await SendError("invalid_payload", "The message must be a valid JSON object.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid request", ct);
                return null;
            }
        }

        try
        {
            var item = first;
            var createIndex = 0;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var type = JsonDictionaryValue.String(item, "type");
                if (type != "response.create")
                {
                    await SendError("unsupported_event", "Supported event types are response.create.");
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Unsupported event", ct);
                    return;
                }
                if (!active.IsCompleted)
                {
                    await SendError("response_in_progress", "Only one response may be active on this connection.");
                    item = await ReceiveAsync() ?? [];
                    if (item.Count == 0) return;
                    continue;
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
                active = Execute(item, generate, ++createIndex);
                await active;
                active = Task.CompletedTask;
                item = await ReceiveAsync() ?? [];
                if (item.Count == 0) break;
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
            try { await active; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (WebSocketException) { }
        }
    }

    private static Dictionary<string, object?>? ProjectRequest(
        Dictionary<string, object?> request,
        IReadOnlyDictionary<string, StoredClientResponse> records,
        out StoredClientResponse? previous)
    {
        previous = null;
        var previousId = JsonDictionaryValue.String(request, "previous_response_id");
        var currentInput = ReadInput(request);
        if (previousId.Length == 0)
        {
            var full = new Dictionary<string, object?>(request, StringComparer.Ordinal);
            full["input"] = currentInput;
            full.Remove("previous_response_id");
            return full;
        }

        if (!records.TryGetValue(previousId, out previous)) return null;
        var previousInput = JsonDictionaryValue.List(previous.Request, "input");
        var previousOutput = JsonDictionaryValue.List(previous.Response, "output");
        var history = previousInput.Concat(previousOutput).ToList();
        var fullRequest = new Dictionary<string, object?>(previous.Request, StringComparer.Ordinal);
        foreach (var pair in request)
        {
            fullRequest[pair.Key] = pair.Value;
        }
        fullRequest["input"] = IsPrefix(history, currentInput)
            ? currentInput
            : history.Concat(currentInput).ToList();
        fullRequest.Remove("previous_response_id");
        return fullRequest;
    }

    private static bool IsPrefix(IReadOnlyList<object?> prefix, IReadOnlyList<object?> values)
    {
        if (values.Count < prefix.Count) return false;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (!JsonValueComparer.AreEqual(prefix[index], values[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static List<object?> ReadInput(IReadOnlyDictionary<string, object?> request)
    {
        var input = JsonDictionaryValue.Get(request, "input");
        if (input is null) return [];
        if (input is string text)
        {
            return
            [
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = text
                }
            ];
        }
        return JsonDictionaryValue.List(request, "input");
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

    private sealed class StoredClientResponse
    {
        public Dictionary<string, object?> Request { get; init; } = [];
        public Dictionary<string, object?> Response { get; init; } = [];
    }
}
