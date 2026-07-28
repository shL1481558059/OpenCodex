using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Controllers;

public sealed class ProxyController : ApiControllerBase
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyEndpointService _proxy;
    private readonly IProxyRequestService _requests;
    private readonly IProxyRouteService _routes;
    private readonly IModelCatalogService _catalog;
    private readonly IProxyLogService _logs;

    public ProxyController(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy,
        IProxyRequestService requests,
        IProxyRouteService routes,
        IModelCatalogService catalog,
        IProxyLogService logs)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
        _requests = requests;
        _routes = routes;
        _catalog = catalog;
        _logs = logs;
    }

    [HttpGet("/models")]
    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models()
    {
        var accessKey = await _requests.AuthenticateAccessKeyAsync(AuthorizationHeader());
        var models = await _routes.ListModelCapabilitiesAsync(accessKey.OwnerUsername);
        var catalogByModel = (_catalog.ListModels(null, null, true).Payload?.Models ?? [])
            .ToDictionary(model => model.ModelKey, StringComparer.OrdinalIgnoreCase);
        var openAiModels = models
            .Select(model => (object?)new Dictionary<string, object?>
            {
                ["id"] = model.Model,
                ["display_name"] = catalogByModel.TryGetValue(model.Model, out var info)
                    ? info.DisplayName
                    : model.Model,
                ["created_at"] = "2024-01-01T00:00:00Z",
                ["type"] = "model"
            })
            .ToList();
        var codexModels = models
            .Select(model => (object?)CodexModelCatalogItem(
                model,
                catalogByModel.TryGetValue(model.Model, out var info) ? info : null))
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = openAiModels,
            ["models"] = codexModels
        };

        return StatusCode(StatusCodes.Status200OK, payload);
    }

    [HttpPost("/responses")]
    [HttpPost("/v1/responses")]
    public Task<IActionResult> Responses()
    {
        return Proxy(ProtocolConverter.Responses);
    }

    [HttpPost("/chat/completions")]
    [HttpPost("/v1/chat/completions")]
    public Task<IActionResult> ChatCompletions()
    {
        return Proxy(ProtocolConverter.Chat);
    }

    [HttpPost("/messages")]
    [HttpPost("/v1/messages")]
    public Task<IActionResult> Messages()
    {
        return Proxy(ProtocolConverter.Messages);
    }

    private async Task<IActionResult> Proxy(string entryProtocol)
    {
        var payload = await _bodyReader.ReadJsonObjectAsync(Request, HttpContext.RequestAborted);

        // 探测请求拦截：Claude Code Desktop 等闭源客户端会高频发送 max_tokens<=1 的最小请求探测渠道可用性。
        // 这些请求会消耗上游配额并触发 429 限流，系统重试机制又使日志量翻倍，严重污染渠道监控。
        // 在代理层直接拦截并返回伪造的最小成功响应，既不消耗上游资源，也保留可观测的拦截日志。
        if (payload is not null && TryGetProbeMaxTokens(payload, out var probeMaxTokens))
        {
            var requestMetadata = ProxyRequestMetadataFactory.FromHttpRequest(
                Request,
                HttpContext.Connection.RemoteIpAddress?.ToString());
            var accessKey = await _requests.AuthenticateAccessKeyAsync(AuthorizationHeader());
            var requestState = _requests.StartRequest();
            var requestModel = JsonDictionaryValue.String(payload, "model");
            var isStream = payload.TryGetValue("stream", out var streamValue) && streamValue is true;

            await _logs.WriteLogAsync(
                new ProxyLogContext(
                    RequestId: requestState.RequestId,
                    OwnerUsername: accessKey.OwnerUsername,
                    ApiKeyId: accessKey.Id,
                    Payload: payload,
                    UpstreamRequest: null,
                    UpstreamResponse: null,
                    ResponsePayload: null,
                    ErrorResponse: null,
                    RequestModel: requestModel,
                    UpstreamModel: null,
                    ChannelId: null,
                    ChannelType: null,
                    IsStream: isStream,
                    TtftMs: null,
                    StatusCode: StatusCodes.Status200OK,
                    DurationMs: 0,
                    Error: $"probe intercepted (max_tokens={probeMaxTokens})",
                    WebSearchDetails: null),
                requestMetadata);

            var probeResponse = BuildProbeResponse(entryProtocol, requestModel, requestState.RequestId);
            return StatusCode(StatusCodes.Status200OK, probeResponse);
        }

        var result = await _proxy.ProxyAsync(
            new ProxyEndpointContext(
                entryProtocol,
                payload,
                AuthorizationHeader(),
                ProxyRequestMetadataFactory.FromHttpRequest(
                    Request,
                    HttpContext.Connection.RemoteIpAddress?.ToString()),
                new ProxyStreamResponseWriter(Response),
                HttpContext.RequestAborted));
        if (result.IsEmpty)
        {
            return new EmptyResult();
        }

        return StatusCode(result.StatusCode, result.Payload);
    }

    /// <summary>
    /// 判断请求是否为探测请求：当 messages/responses/chat 协议下的最大输出 token 限制 <= 1 时视为探测。
    /// </summary>
    private static bool TryGetProbeMaxTokens(Dictionary<string, object?> payload, out int maxTokens)
    {
        maxTokens = 0;
        foreach (var key in new[] { "max_tokens", "max_output_tokens", "max_completion_tokens" })
        {
            if (payload.TryGetValue(key, out var value) && value is int intValue && intValue <= 1)
            {
                maxTokens = intValue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按入口协议构造伪造的最小成功响应，使客户端认为探测通过。
    /// </summary>
    private static Dictionary<string, object?> BuildProbeResponse(
        string entryProtocol,
        string? model,
        string requestId)
    {
        var shortId = requestId.Replace("-", string.Empty);
        if (shortId.Length > 24)
        {
            shortId = shortId[..24];
        }

        if (entryProtocol == ProtocolConverter.Messages)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = $"msg_{shortId}",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model ?? "claude-opus-5",
                ["content"] = Array.Empty<object>(),
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = null
            };
        }

        if (entryProtocol == ProtocolConverter.Chat)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = $"chatcmpl-{shortId}",
                ["object"] = "chat.completion",
                ["created"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model ?? "gpt-5.5",
                ["choices"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty
                        },
                        ["finish_reason"] = "stop"
                    }
                }
            };
        }

        // responses 协议
        return new Dictionary<string, object?>
        {
            ["id"] = $"resp_{shortId}",
            ["object"] = "response",
            ["status"] = "completed",
            ["model"] = model ?? "gpt-5.5",
            ["output"] = Array.Empty<object>()
        };
    }

    private string? AuthorizationHeader()
    {
        return Request.Headers.TryGetValue("Authorization", out var values)
            ? values.ToString()
            : null;
    }

    private static Dictionary<string, object?> CodexModelCatalogItem(
        ProxyModelCapabilityDto model,
        ModelInfoResponse? info)
    {
        if (info is not null && info.Catalog.Count > 0)
        {
            var catalog = info.Catalog.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            catalog["slug"] = model.Model;
            catalog["display_name"] = info.DisplayName;
            if (!catalog.ContainsKey("input_modalities"))
            {
                catalog["input_modalities"] = model.SupportsImage
                    ? new List<object?> { "text", "image" }
                    : new List<object?> { "text" };
            }
            catalog["supports_image_detail_original"] = model.SupportsImage;
            if (!catalog.ContainsKey("additional_speed_tiers"))
            {
                catalog["additional_speed_tiers"] = new List<object?> { "fast" };
            }
            return catalog;
        }

        var inputModalities = model.SupportsImage
            ? new List<object?> { "text", "image", "audio", "video" }
            : new List<object?> { "text" };

        return new Dictionary<string, object?>
        {
            ["slug"] = model.Model,
            ["display_name"] = model.Model,
            ["description"] = $"OpenCodex routed model: {model.Model}.",
            ["default_reasoning_level"] = "medium",
            ["supported_reasoning_levels"] = new List<object?>
            {
                ReasoningLevel("low", "Quick responses with lighter reasoning"),
                ReasoningLevel("medium", "Balances speed and reasoning depth for everyday tasks"),
                ReasoningLevel("high", "Greater reasoning depth for complex problems"),
                ReasoningLevel("xhigh", "Extra high reasoning depth for extremely complex logic")
            },
            ["shell_type"] = "shell_command",
            ["visibility"] = "list",
            ["minimal_client_version"] = "1.0.0",
            ["supported_in_api"] = true,
            ["availability_nux"] = null,
            ["upgrade"] = null,
            ["priority"] = 100,
            ["base_instructions"] = "You are an OpenCodex routed coding agent. Help the user by inspecting the workspace, making focused changes, and reporting results clearly and efficiently.",
            ["model_messages"] = new Dictionary<string, object?>
            {
                ["instructions_template"] = "{{ personality }}",
                ["instructions_variables"] = new Dictionary<string, object?>
                {
                    ["personality_default"] = string.Empty,
                    ["personality_friendly"] = string.Empty,
                    ["personality_pragmatic"] = string.Empty
                }
            },
            ["support_verbosity"] = true,
            ["default_verbosity"] = "medium",
            ["apply_patch_tool_type"] = "freeform",
            ["web_search_tool_type"] = "text",
            ["input_modalities"] = inputModalities,
            ["supports_image_detail_original"] = model.SupportsImage,
            ["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = 256000
            },
            ["supports_parallel_tool_calls"] = true,
            ["context_window"] = 256000,
            ["max_context_window"] = 256000,
            ["auto_compact_token_limit"] = null,
            ["reasoning_summary_format"] = "text",
            ["default_reasoning_summary"] = "short",
            ["supports_reasoning_summaries"] = true,
            ["additional_speed_tiers"] = new List<object?> { "fast" },
            ["service_tiers"] = new List<object?> { "standard", "pro" },
            ["available_in_plans"] = new List<object?> { "free","plus", "team", "enterprise" },
            ["prefer_websockets"] = true,
            ["experimental_supported_tools"] = new List<object?> { "code_interpreter", "web_browser" },
            ["supports_search_tool"] = true
        };
    }

    private static Dictionary<string, object?> ReasoningLevel(string effort, string description)
    {
        return new Dictionary<string, object?>
        {
            ["effort"] = effort,
            ["description"] = description
        };
    }
}
