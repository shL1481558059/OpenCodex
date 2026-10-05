using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Services;

public sealed partial class MultiAgentResponseService
{
    private NativeClientSessionStore NativeSessions => nativeSessions;

    public async Task<IActionResult?> TryClientResponsesAsync(Dictionary<string, object?> request, bool allowCreate = false)
    {
        if (JsonDictionaryValue.Get(JsonDictionaryValue.Object(request, "multi_agent", WebSearchPayload.DeepCopyObject), "enabled") is false)
            return null;
        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(Request, HttpContext.Connection.RemoteIpAddress?.ToString());
        var identity = MultiAgentClientIdentity.Parse(metadata.Headers);
        var session = await ResolveClientBinding(request, identity, allowCreate, HttpContext.RequestAborted);
        if (session is null) return null;

        Response.Headers["X-OpenCodex-Multi-Agent-Session"] = session.RootThreadId;
        var stream = JsonDictionaryValue.Get(request, "stream") is true;
        async Task Emit(Dictionary<string, object?> item)
        {
            if (!stream) return;
            if (!Response.HasStarted) ProxyStreamResponseWriter.PrepareSse(Response);
            await Response.WriteAsync($"event: {JsonDictionaryValue.String(item, "type")}\ndata: {JsonSerializer.Serialize(item)}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }

        var result = await ExecuteClient(request, session, metadata, Emit, HttpContext.RequestAborted);
        return stream ? new EmptyResult() : new ObjectResult(result) { StatusCode = StatusCodes.Status200OK };
    }

    private async Task<NativeClientSession?> ResolveClientBinding(Dictionary<string, object?> request,
        MultiAgentClientIdentity? identity, bool allowCreate, CancellationToken ct)
    {
        if (identity is null)
        {
            if (allowCreate && MultiAgentClientTools.FromRequest(request).Definitions.Count > 0)
                throw new BadRequestException("Native collaboration requires a client thread identity. Supply thread-id or x-codex-turn-metadata.thread_id.");
            return null;
        }
        var owner = identityContext.RequireIdentity().ApiKeyId;
        var existing = await NativeSessions.TryGetAsync(owner, identity.ThreadId, ct);
        var parent = identity.ParentThreadId.Length == 0 ? null
            : await NativeSessions.TryGetAsync(owner, identity.ParentThreadId, ct);
        if (!allowCreate && existing is null && parent is null) return null;
        if (JsonDictionaryValue.String(request, "model").Length == 0)
            throw new BadRequestException("model is required.");
        // Client thread identity establishes native ownership even when this request
        // has no collaboration tools (for example during a client checkpoint).
        return await NativeSessions.ResolveAsync(owner, identity,
            JsonDictionaryValue.Get(request, "store") is not false,
            ConcurrentLimit(JsonDictionaryValue.Object(request, "multi_agent", WebSearchPayload.DeepCopyObject)), ct);
    }

    private async Task<Dictionary<string, object?>> ExecuteClient(Dictionary<string, object?> request,
        NativeClientSession session, ProxyRequestMetadata metadata,
        Func<Dictionary<string, object?>, Task> emit, CancellationToken ct, bool requestScopedTurn = true)
    {
        var executor = new NativeClientResponseExecutor(NativeSessions);
        var turn = requestScopedTurn ? MultiAgentClientIdentity.Parse(metadata.Headers)?.TurnId ?? "" : "";
        logger.LogDebug("Native client response: thread={ThreadId} root={RootThreadId} parent={ParentThreadId} turn={TurnId} transport={Transport}",
            session.ThreadId, session.RootThreadId, session.ParentThreadId, turn, requestScopedTurn ? "http" : "websocket");
        try
        {
            var result = await executor.ExecuteAsync(session, request, turn,
                (payload, onEvent, token) => CallModelAsync(payload, metadata, onEvent, token), emit, ct);
            var status = JsonDictionaryValue.String(result, "status");
            if (status == "failed")
            {
                var error = JsonDictionaryValue.Object(result, "error", WebSearchPayload.DeepCopyObject);
                logger.LogWarning("Native client response failed: thread={ThreadId} turn={TurnId} response={ResponseId} code={ErrorCode} reason={Reason}",
                    session.ThreadId, turn, JsonDictionaryValue.String(result, "id"),
                    JsonDictionaryValue.String(error, "code"), JsonDictionaryValue.String(error, "message"));
            }
            else
                logger.LogDebug("Native client response finished: thread={ThreadId} turn={TurnId} response={ResponseId} status={Status}",
                    session.ThreadId, turn, JsonDictionaryValue.String(result, "id"), status);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Native client response cancelled: thread={ThreadId} turn={TurnId}", session.ThreadId, turn);
            throw;
        }
    }
}
