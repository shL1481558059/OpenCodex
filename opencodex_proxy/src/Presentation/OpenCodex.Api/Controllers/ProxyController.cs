using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Controllers;

public sealed class ProxyController : ApiControllerBase
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyEndpointService _proxy;
    private readonly IProxyRequestService _requests;
    private readonly IProxyRouteService _routes;

    public ProxyController(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy,
        IProxyRequestService requests,
        IProxyRouteService routes)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
        _requests = requests;
        _routes = routes;
    }

    [HttpGet("/models")]
    [HttpGet("/v1/models")]
    public IActionResult Models()
    {
        var accessKey = _requests.AuthenticateAccessKey(AuthorizationHeader());
        var models = _routes.ListModelCapabilities(accessKey.OwnerUsername);
        var openAiModels = models
            .Select(model => (object?)new Dictionary<string, object?>
            {
                ["id"] = model.Model,
                ["display_name"] =  model.Model,
                ["created_at"] = "2024-01-01T00:00:00Z",
                ["type"] = "model"
            })
            .ToList();
        // var codexModels = models
        //     .Select(model => (object?)CodexModelCatalogItem(model))
        //     .ToList();

        return StatusCode(
            StatusCodes.Status200OK,
            new Dictionary<string, object?>
            {
                ["object"] = "list",
                ["data"] = openAiModels,
                //["models"] = codexModels
            });
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

    private string? AuthorizationHeader()
    {
        return Request.Headers.TryGetValue("Authorization", out var values)
            ? values.ToString()
            : null;
    }

    private static Dictionary<string, object?> CodexModelCatalogItem(ProxyModelCapabilityDto model)
    {
        // 针对 GPT-5.5 扩展了潜在的多模态输入支持
        var inputModalities = model.SupportsImage
            ? new List<object?> { "text", "image", "audio", "video" }
            : new List<object?> { "text" };

        return new Dictionary<string, object?>
        {
            ["slug"] = model.Model,
            ["display_name"] = model.Model,
            ["description"] = $"GPT-5.5 architecture routed model: {model.Model}.",
            ["default_reasoning_level"] = "fast", // 将默认推理级别调整为 fast（如果需要保持平衡可改回 medium）
            ["supported_reasoning_levels"] = new List<object?>
            {
                ReasoningLevel("low", "Quick responses with lighter reasoning"),
                ReasoningLevel("medium", "Balances speed and reasoning depth for everyday tasks"),
                ReasoningLevel("high", "Greater reasoning depth for complex problems"),
                ReasoningLevel("xhigh", "Extra high reasoning depth for extremely complex logic")
            },
            ["shell_type"] = "shell_command",
            ["visibility"] = "list",
            ["minimal_client_version"] = "1.0.0", // 提升客户端版本要求
            ["supported_in_api"] = true,
            ["availability_nux"] = null,
            ["upgrade"] = null,
            ["priority"] = 100,
            ["base_instructions"] = "You are an advanced GPT-5.5 coding agent. Help the user by inspecting the workspace, making focused changes, and reporting results clearly and efficiently.",
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
            ["support_verbosity"] = true, // 升级为支持冗长控制
            ["default_verbosity"] = "medium",
            ["apply_patch_tool_type"] = "freeform",
            ["web_search_tool_type"] = "text",
            ["input_modalities"] = inputModalities,
            ["supports_image_detail_original"] = model.SupportsImage,
            ["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = 256000 // 对应 GPT-5.5 的更大上下文截断限制
            },
            ["supports_parallel_tool_calls"] = true,
            ["context_window"] = 256000, // 提升上下文窗口至 256k
            ["max_context_window"] = 256000,
            ["auto_compact_token_limit"] = null,
            ["reasoning_summary_format"] = "text", // 启用推理摘要格式
            ["default_reasoning_summary"] = "short",
            ["supports_reasoning_summaries"] = true, // GPT-5.5 原生支持推理过程摘要
            ["additional_speed_tiers"] = new List<object?> { "fast" }, // 在速度层级中显式声明支持 fast
            ["service_tiers"] = new List<object?> { "standard", "pro" },
            ["available_in_plans"] = new List<object?> { "free","plus", "team", "enterprise" },
            ["prefer_websockets"] = true, // 偏向 WebSockets 以实现低延迟输出
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
