using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
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
        var modelIds = _routes.ListModels(accessKey.OwnerUsername);
        var openAiModels = modelIds
            .Select(model => (object?)new Dictionary<string, object?>
            {
                ["id"] = model,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "opencodex"
            })
            .ToList();
        var codexModels = modelIds
            .Select(model => (object?)CodexModelCatalogItem(model))
            .ToList();

        return StatusCode(
            StatusCodes.Status200OK,
            new Dictionary<string, object?>
            {
                ["object"] = "list",
                ["data"] = openAiModels,
                ["models"] = codexModels
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

    private static Dictionary<string, object?> CodexModelCatalogItem(string model)
    {
        return new Dictionary<string, object?>
        {
            ["slug"] = model,
            ["display_name"] = model,
            ["description"] = $"OpenCodex routed model {model}.",
            ["default_reasoning_level"] = "medium",
            ["supported_reasoning_levels"] = new List<object?>
            {
                ReasoningLevel("low", "Fast responses with lighter reasoning"),
                ReasoningLevel("medium", "Balances speed and reasoning depth for everyday tasks"),
                ReasoningLevel("high", "Greater reasoning depth for complex problems"),
                ReasoningLevel("xhigh", "Extra high reasoning depth for complex problems")
            },
            ["shell_type"] = "shell_command",
            ["visibility"] = "list",
            ["minimal_client_version"] = "0.0.1",
            ["supported_in_api"] = true,
            ["availability_nux"] = null,
            ["upgrade"] = null,
            ["priority"] = 100,
            ["base_instructions"] = "You are Codex, a coding agent. Help the user by inspecting the workspace, making focused changes, and reporting results clearly.",
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
            ["support_verbosity"] = false,
            ["default_verbosity"] = "medium",
            ["apply_patch_tool_type"] = "freeform",
            ["web_search_tool_type"] = "text",
            ["input_modalities"] = new List<object?> { "text" },
            ["supports_image_detail_original"] = false,
            ["truncation_policy"] = new Dictionary<string, object?>
            {
                ["mode"] = "tokens",
                ["limit"] = 10000
            },
            ["supports_parallel_tool_calls"] = true,
            ["context_window"] = 128000,
            ["max_context_window"] = 128000,
            ["auto_compact_token_limit"] = null,
            ["reasoning_summary_format"] = "none",
            ["default_reasoning_summary"] = "none",
            ["supports_reasoning_summaries"] = false,
            ["additional_speed_tiers"] = new List<object?>(),
            ["service_tiers"] = new List<object?>(),
            ["available_in_plans"] = new List<object?>(),
            ["prefer_websockets"] = false,
            ["experimental_supported_tools"] = new List<object?>(),
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
