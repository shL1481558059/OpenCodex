using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Api.Services;

/// <summary>
/// 代理端点统一处理：responses / chat.completions / messages 转发，以及模型列表组装。
/// </summary>
public sealed class ProxyService : IProxyService
{
    private readonly IRequestBodyReader _bodyReader;
    private readonly IProxyEndpointService _proxy;
    private readonly IProxyRequestService _requests;
    private readonly IProxyRouteService _routes;
    private readonly IModelCatalogService _catalog;
    private readonly ICodexOfficialModelCatalogService _codexModels;
    private readonly IProxySettingsService _proxySettings;
    private readonly IProxyLogService _logs;

    public ProxyService(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy,
        IProxyRequestService requests,
        IProxyRouteService routes,
        IModelCatalogService catalog,
        ICodexOfficialModelCatalogService codexModels,
        IProxySettingsService proxySettings,
        IProxyLogService logs)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
        _requests = requests;
        _routes = routes;
        _catalog = catalog;
        _codexModels = codexModels;
        _proxySettings = proxySettings;
        _logs = logs;
    }

    public async Task<IActionResult> ModelsAsync(HttpRequest request, HttpResponse response)
    {
        var accessKey = await _requests.AuthenticateAccessKeyAsync(
            RequestHeaders(request));
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

        if (IsCodexClient(request))
        {
            return StatusCodeResult(
                response,
                new Dictionary<string, object?>
                {
                    ["models"] = _codexModels.BuildCodexModels(models, catalogByModel)
                });
        }

        var payload = new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = openAiModels,
            ["models"] = codexModels
        };

        return StatusCodeResult(response, payload);
    }

    public async Task<IActionResult> ProxyAsync(
        string entryProtocol,
        HttpRequest request,
        HttpResponse response)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = await _bodyReader.ReadJsonObjectAsync(request, request.HttpContext.RequestAborted);
        var authorization = RequestHeaders(request);
        var probeRequestId = Guid.NewGuid().ToString();
        if (payload is not null
            && _proxySettings.GetBool("intercept_probe_requests", false)
            && ProbeRequestInterceptor.TryIntercept(
                entryProtocol,
                payload,
                probeRequestId,
                out var probeResult))
        {
            var accessKey = await _requests.AuthenticateAccessKeyAsync(authorization);
            var requestMetadata = ProxyRequestMetadataFactory.FromHttpRequest(
                request,
                request.HttpContext.Connection.RemoteIpAddress?.ToString());
            var responsePayload = probeResult!.Payload as Dictionary<string, object?>;
            await _logs.WriteLogAsync(
                new ProxyLogContext(
                    probeRequestId,
                    accessKey.OwnerUsername,
                    accessKey.Id,
                    payload,
                    UpstreamRequest: null,
                    UpstreamResponse: null,
                    ResponsePayload: responsePayload,
                    ErrorResponse: null,
                    RequestModel: payload.TryGetValue("model", out var modelValue)
                        ? modelValue?.ToString()
                        : null,
                    UpstreamModel: null,
                    ChannelId: null,
                    ChannelType: null,
                    IsStream: false,
                    TtftMs: null,
                    StatusCode: probeResult.StatusCode,
                    DurationMs: (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Error: null,
                    WebSearchDetails: null),
                requestMetadata);
            if (probeResult!.IsEmpty)
            {
                return new EmptyResult();
            }

            return StatusCodeResult(response, probeResult.StatusCode, probeResult.Payload);
        }

        var result = await _proxy.ProxyAsync(
            new ProxyEndpointContext(
                entryProtocol,
                payload,
                authorization,
                ProxyRequestMetadataFactory.FromHttpRequest(
                    request,
                    request.HttpContext.Connection.RemoteIpAddress?.ToString()),
                new ProxyStreamResponseWriter(response),
                request.HttpContext.RequestAborted));
        if (result.IsEmpty)
        {
            return new EmptyResult();
        }

        return StatusCodeResult(response, result.StatusCode, result.Payload);
    }

    private static bool IsCodexClient(HttpRequest request)
    {
        if (request.Query.ContainsKey("client_version"))
        {
            return true;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        return userAgent.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Codex Desktop", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RequestHeaders(HttpRequest request)
    {
        return request.Headers.TryGetValue("Authorization", out var values)
            ? values.ToString()
            : null;
    }

    private static IActionResult StatusCodeResult(
        HttpResponse response,
        object? value)
    {
        return new ObjectResult(value) { StatusCode = StatusCodes.Status200OK };
    }

    private static IActionResult StatusCodeResult(
        HttpResponse response,
        int statusCode,
        object? value)
    {
        return new ObjectResult(value) { StatusCode = statusCode };
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
                new Dictionary<string, object?>
                {
                    ["effort"] = "low",
                    ["description"] = "Quick responses with lighter reasoning"
                },
                new Dictionary<string, object?>
                {
                    ["effort"] = "medium",
                    ["description"] = "Balances speed and reasoning depth for everyday tasks"
                },
                new Dictionary<string, object?>
                {
                    ["effort"] = "high",
                    ["description"] = "Greater reasoning depth for complex problems"
                },
                new Dictionary<string, object?>
                {
                    ["effort"] = "xhigh",
                    ["description"] = "Extra high reasoning depth for extremely complex logic"
                }
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
            ["default_reasoning_summary"] = "auto",
            ["supports_reasoning_summaries"] = true,
            ["additional_speed_tiers"] = new List<object?> { "fast" },
            ["service_tiers"] = new List<object?> { "standard", "pro" },
            ["available_in_plans"] = new List<object?> { "free","plus", "team", "enterprise" },
            ["prefer_websockets"] = true,
            ["experimental_supported_tools"] = new List<object?> { "code_interpreter", "web_browser" },
            ["supports_search_tool"] = true
        };
    }
}
