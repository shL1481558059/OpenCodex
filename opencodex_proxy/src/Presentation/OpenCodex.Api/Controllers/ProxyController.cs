using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
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

    public ProxyController(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy,
        IProxyRequestService requests,
        IProxyRouteService routes,
        IModelCatalogService catalog)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
        _requests = requests;
        _routes = routes;
        _catalog = catalog;
    }

    [HttpGet("/models")]
    [HttpGet("/v1/models")]
    public IActionResult Models()
    {
        var accessKey = _requests.AuthenticateAccessKey(AuthorizationHeader());
        var models = _routes.ListModelCapabilities(accessKey.OwnerUsername);
        var catalogByModel = (_catalog.ListModels(null, null, "global", true, null).Payload?.Models ?? [])
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
            ["data"] = openAiModels
        };
        if (IncludeCodexCatalog())
        {
            payload["models"] = codexModels;
        }

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

    private bool IncludeCodexCatalog()
    {
        return Request.Query.TryGetValue("codex_catalog", out var value)
            && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
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
