using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
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
        var catalogModels = _catalog.BuildProxyModelCatalog(models);

        if (IsCodexClient(request))
        {
            var merged = BuildCodexClientModels(catalogModels);
            return StatusCodeResult(
                response,
                new Dictionary<string, object?>
                {
                    ["models"] = merged
                });
        }

        var openAiModels = catalogModels
            .Select(model => (object?)new Dictionary<string, object?>
            {
                ["id"] = model.TryGetValue("slug", out var slug) ? slug : null,
                ["display_name"] = model.TryGetValue("display_name", out var displayName)
                    ? displayName
                    : null,
                ["created_at"] = "2024-01-01T00:00:00Z",
                ["type"] = "model"
            })
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = openAiModels,
            ["models"] = catalogModels
        };

        return StatusCodeResult(response, payload);
    }

    private List<Dictionary<string, object?>> BuildCodexClientModels(
        IReadOnlyList<Dictionary<string, object?>> catalogModels)
    {
        var gptModels = _codexModels.BuildCodexGptModels();
        var gptSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpt in gptModels)
        {
            if (gpt.TryGetValue("slug", out var value) && value is string slug)
            {
                gptSlugs.Add(slug);
            }
        }

        var merged = new List<Dictionary<string, object?>>(gptModels);
        foreach (var catalog in catalogModels)
        {
            if (catalog.TryGetValue("slug", out var value)
                && value is string slug
                && !gptSlugs.Contains(slug))
            {
                merged.Add(catalog);
            }
        }

        return merged;
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

    private static string? RequestHeaders(HttpRequest request)
    {
        return request.Headers.TryGetValue("Authorization", out var values)
            ? values.ToString()
            : null;
    }

    private static bool IsCodexClient(HttpRequest request)
    {
        if (request.Query.ContainsKey("client_version"))
        {
            return true;
        }

        var userAgent = request.Headers.UserAgent.ToString();
        return userAgent.Contains("codex", StringComparison.OrdinalIgnoreCase);
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
}
