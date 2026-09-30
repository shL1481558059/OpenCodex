using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Authentication;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

public sealed partial class MultiAgentResponseService(
    IHttpContextAccessor accessor,
    OpenCodex.CoreBase.Services.IModelCatalogService catalog,
    IProxyIdentityContext identityContext,
    IProxyEndpointService proxyEndpoint,
    MultiAgentRunStore store,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<MultiAgentResponseService> logger)
{
    private HttpContext HttpContext => accessor.HttpContext!;
    private HttpRequest Request => HttpContext.Request;
    private HttpResponse Response => HttpContext.Response;

    public async Task<IActionResult> Responses(Dictionary<string, object?> request)
    {
        var ct = HttpContext.RequestAborted;
        var metadata = ProxyRequestMetadataFactory.FromHttpRequest(Request, HttpContext.Connection.RemoteIpAddress?.ToString());
        var options = JsonDictionaryValue.Object(request, "multi_agent", WebSearchPayload.DeepCopyObject);
        if (JsonDictionaryValue.Get(options, "enabled") is false)
        {
            var direct = WebSearchPayload.DeepCopyObject(request);
            direct.Remove("multi_agent");
            var result = await proxyEndpoint.ProxyAsync(new ProxyEndpointContext(ProtocolConverter.Responses,
                direct, new ProxyRequestMetadata(metadata.Method, metadata.Path, metadata.ClientIp, metadata.Headers),
                new ProxyStreamResponseWriter(Response), ct));
            return result.IsEmpty ? new EmptyResult() : new ObjectResult(result.Payload) { StatusCode = result.StatusCode };
        }

        var model = JsonDictionaryValue.String(request, "model");
        if (model.Length == 0) throw new BadRequestException("model is required.");
        var session = SessionId(request);
        Response.Headers["X-OpenCodex-Multi-Agent-Session"] = session;
        var owner = identityContext.RequireIdentity().ApiKeyId;
        MultiAgentRun run;
        try
        {
            run = await store.GetAsync($"{owner:N}:{session}", JsonDictionaryValue.String(request, "previous_response_id"),
                () => new MultiAgentRun
                {
                    Model = model,
                    Template = MultiAgentProtocol.NormalizeRequest(request),
                    MaxConcurrentSubagents = ConcurrentLimit(options),
                    CompactThresholdTokens = CompactThreshold(request),
                    Agents = new Dictionary<string, MultiAgentState>(StringComparer.Ordinal)
                    {
                        ["/root"] = new() { Name = "/root", History = MultiAgentProtocol.InitialHistory(request) }
                    }
                }, ct);
        }
        catch (KeyNotFoundException)
        {
            throw new BadRequestException("The previous response is unavailable for this API key and session. Reuse the session identifier or start a new conversation.");
        }

        var gate = store.Gate(run);
        await gate.WaitAsync(ct);
        try
        {
            if (run.Model != model) throw new BadRequestException("A multi-agent session keeps its initial model. Start a new session to change models.");
            var stream = JsonDictionaryValue.Get(request, "stream") is true;
            var persist = JsonDictionaryValue.Get(request, "store") is not false;
            var sequence = 0;
            async Task Emit(Dictionary<string, object?> item)
            {
                if (!stream) return;
                if (!Response.HasStarted) ProxyStreamResponseWriter.PrepareSse(Response);
                sequence = Math.Max(sequence, WebSearchPayload.ToInt(JsonDictionaryValue.Get(item, "sequence_number"), sequence));
                await Response.WriteAsync($"event: {JsonDictionaryValue.String(item, "type")}\ndata: {JsonSerializer.Serialize(item)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }

            async Task<Dictionary<string, object?>> CallModel(Dictionary<string, object?> payload, Func<Dictionary<string, object?>, CancellationToken, Task> onEvent, CancellationToken token)
            {
                return await CallModelAsync(payload, metadata, onEvent, token);
            }

            var runtime = new MultiAgentRuntime(run, CallModel, Emit,
                () => store.SaveAsync(run, CancellationToken.None, persist));
            try
            {
                var response = await runtime.ExecuteAsync(request, ct);
                return stream ? new EmptyResult() : new ObjectResult(response) { StatusCode = StatusCodes.Status200OK };
            }
            catch (Exception exception) when (Response.HasStarted && !ct.IsCancellationRequested)
            {
                logger.LogError(exception, "Multi-agent response failed for run {RunId}", run.Id);
                await Emit(new Dictionary<string, object?>
                {
                    ["type"] = "response.failed", ["sequence_number"] = ++sequence,
                    ["response"] = new Dictionary<string, object?>
                    {
                        ["id"] = run.LastResponseId, ["object"] = "response", ["status"] = "failed",
                        ["model"] = run.Model, ["output"] = new List<object?>(),
                        ["error"] = new Dictionary<string, object?>
                        {
                            ["code"] = exception is ProxyException proxy ? proxy.ErrorType : "server_error",
                            ["message"] = exception is BadRequestException ? exception.Message : "The multi-agent response failed. Check server logs before retrying."
                        }
                    }
                });
                return new EmptyResult();
            }
        }
        finally { gate.Release(); }
    }

    private string SessionId(Dictionary<string, object?> request)
    {
        var metadata = JsonDictionaryValue.Object(request, "client_metadata", WebSearchPayload.DeepCopyObject);
        var candidates = new[]
        {
            JsonDictionaryValue.String(metadata, "session_id"), JsonDictionaryValue.String(metadata, "thread_id"),
            Request.Headers["session-id"].ToString(), Request.Headers["X-OpenCodex-Multi-Agent-Session"].ToString(),
            JsonDictionaryValue.String(request, "prompt_cache_key")
        };
        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? Guid.NewGuid().ToString("N");
    }

    private int CompactThreshold(Dictionary<string, object?> request)
    {
        var context = JsonDictionaryValue.Object(request, "context_management", WebSearchPayload.DeepCopyObject);
        var value = WebSearchPayload.ToInt(JsonDictionaryValue.Get(context, "compact_threshold"),
            configuration.GetValue("MultiAgent:CompactThresholdTokens", 64000));
        if (value < 1) throw new BadRequestException("context_management.compact_threshold must be positive.");
        return value;
    }

    private static int ConcurrentLimit(Dictionary<string, object?> options)
    {
        var value = JsonDictionaryValue.Get(options, "max_concurrent_subagents");
        if (value is null) return 3;
        var limit = WebSearchPayload.ToInt(value, 0);
        if (limit < 1) throw new BadRequestException("multi_agent.max_concurrent_subagents must be positive.");
        return limit;
    }

    private async Task<Dictionary<string, object?>> CallModelAsync(Dictionary<string, object?> payload,
        ProxyRequestMetadata metadata, Func<Dictionary<string, object?>, CancellationToken, Task> onEvent, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var endpoint = scope.ServiceProvider.GetRequiredService<IProxyEndpointService>();
        payload["stream"] = true;
        var writer = new MultiAgentModelStreamWriter(onEvent);
        var result = await endpoint.ProxyAsync(new ProxyEndpointContext(ProtocolConverter.Responses, payload,
            new ProxyRequestMetadata(metadata.Method, metadata.Path, metadata.ClientIp, metadata.Headers),
            writer, token));
        if (result.StatusCode != StatusCodes.Status200OK)
            throw new UpstreamException("A multi-agent model request failed.", result.StatusCode, result.Payload);
        return writer.Result ?? throw new UpstreamException("The model stream returned no Responses result.");
    }
}
