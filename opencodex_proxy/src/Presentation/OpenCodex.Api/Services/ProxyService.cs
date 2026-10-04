using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs;
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
    private readonly IProxyIdentityContext _identity;
    private readonly IProxyRouteService _routes;
    private readonly IModelCatalogService _catalog;
    private readonly ICodexOfficialModelCatalogService _codexModels;
    private readonly IProxySettingsService _proxySettings;
    private readonly IProxyLogService _logs;

    public ProxyService(
        IRequestBodyReader bodyReader,
        IProxyEndpointService proxy,
        IProxyIdentityContext identity,
        IProxyRouteService routes,
        IModelCatalogService catalog,
        ICodexOfficialModelCatalogService codexModels,
        IProxySettingsService proxySettings,
        IProxyLogService logs)
    {
        _bodyReader = bodyReader;
        _proxy = proxy;
        _identity = identity;
        _routes = routes;
        _catalog = catalog;
        _codexModels = codexModels;
        _proxySettings = proxySettings;
        _logs = logs;
    }

    public async Task<IActionResult> ModelsAsync(HttpRequest request, HttpResponse response)
    {
        var identity = _identity.RequireIdentity();
        var models = await _routes.ListModelCapabilitiesAsync(identity.OwnerUsername);
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

        // 数据库目录是长度字段的唯一来源：同 slug 时用目录值覆盖模板值，
        // 目录缺失或非正数时保留模板值。
        var catalogBySlug = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogModels)
        {
            if (catalog.TryGetValue("slug", out var value)
                && value is string catalogSlug
                && catalogSlug.Length > 0)
            {
                catalogBySlug.TryAdd(catalogSlug, catalog);
            }
        }

        var mergedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<Dictionary<string, object?>>(gptModels.Count);
        foreach (var gpt in gptModels)
        {
            if (gpt.TryGetValue("slug", out var value)
                && value is string slug
                && slug.Length > 0)
            {
                mergedSlugs.Add(slug);
                if (catalogBySlug.TryGetValue(slug, out var catalog))
                {
                    ApplyCatalogLengthOverrides(gpt, catalog);
                }
            }

            merged.Add(gpt);
        }

        foreach (var catalog in catalogModels)
        {
            if (catalog.TryGetValue("slug", out var value)
                && value is string slug
                && slug.Length > 0
                && mergedSlugs.Add(slug))
            {
                merged.Add(catalog);
            }
        }

        NormalizeCodexInstructions(merged);

        return merged;
    }

    /// <summary>
    /// Codex 客户端对模型目录响应体有 1 MiB 硬上限，超限会整份丢弃并回退内置元数据
    /// (272000 × 95% = 258400)，表现为上下文只用到约 246k 就触发压缩。
    /// 同一模型的 base_instructions 与 model_messages.instructions_template 内容等价，
    /// 两者同时下发会让目录膨胀到约 1.07 MB，因此这里统一只保留 base_instructions。
    /// 同时清理未被客户端渲染、会原样进入提示词的 {{ personality }} 占位符。
    /// </summary>
    private static void NormalizeCodexInstructions(
        IReadOnlyList<Dictionary<string, object?>> models)
    {
        foreach (var model in models)
        {
            NormalizeCodexInstructions(model);
        }
    }

    private static void NormalizeCodexInstructions(Dictionary<string, object?> model)
    {
        const string templateKey = "instructions_template";
        const string personalityPlaceholder = "{{ personality }}";

        var instructions = ReadRawString(model, "base_instructions");
        if (instructions.Length == 0
            && AsObjectDictionary(model, "model_messages") is { } messages)
        {
            instructions = ReadRawString(messages, templateKey);
        }

        instructions = instructions.Replace(
            personalityPlaceholder,
            string.Empty,
            StringComparison.Ordinal);
        if (instructions.Length > 0)
        {
            model["base_instructions"] = instructions;
        }

        if (AsObjectDictionary(model, "model_messages") is not { } modelMessages
            || !modelMessages.ContainsKey(templateKey))
        {
            return;
        }

        // model_messages 可能是 CodexModelInstructions 的共享单例，必须复制后再删键。
        var normalizedMessages = new Dictionary<string, object?>(modelMessages, StringComparer.Ordinal);
        normalizedMessages.Remove(templateKey);
        model["model_messages"] = normalizedMessages;
    }

    private static string ReadRawString(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static void ApplyCatalogLengthOverrides(
        Dictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> catalog)
    {
        if (ReadPositiveLong(catalog, "context_window") is { } contextWindow)
        {
            target["context_window"] = contextWindow;
        }

        if (ReadPositiveLong(catalog, "max_context_window") is { } maxContextWindow)
        {
            target["max_context_window"] = maxContextWindow;
        }

        if (ReadPositiveLong(catalog, "effective_context_window_percent") is { } effectivePercent
            && effectivePercent is >= 1 and <= 100)
        {
            target["effective_context_window_percent"] = effectivePercent;
        }

        if (AsObjectDictionary(catalog, "truncation_policy") is not { } policy)
        {
            return;
        }

        var mergedPolicy = AsObjectDictionary(target, "truncation_policy") is { } existing
            ? new Dictionary<string, object?>(existing, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        if (policy.TryGetValue("mode", out var modeValue)
            && modeValue is string mode
            && mode.Length > 0)
        {
            mergedPolicy["mode"] = mode;
        }

        if (ReadPositiveLong(policy, "limit") is { } limit)
        {
            mergedPolicy["limit"] = limit;
        }

        target["truncation_policy"] = mergedPolicy;
    }

    private static Dictionary<string, object?>? AsObjectDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            Dictionary<string, object?> dictionary => dictionary,
            IReadOnlyDictionary<string, object?> readOnly => readOnly.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            JsonElement { ValueKind: JsonValueKind.Object } element => element
                .EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => JsonRequestValue.Value(property.Value),
                    StringComparer.Ordinal),
            _ => null
        };
    }

    private static long? ReadPositiveLong(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value))
        {
            return null;
        }

        var number = value switch
        {
            int integer => integer,
            long longValue => longValue,
            double fraction => (long)fraction,
            decimal decimalValue => (long)decimalValue,
            string text when long.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed) => parsed,
            _ => 0L
        };
        return number > 0 ? number : null;
    }

    public async Task<IActionResult> ProxyAsync(
        string entryProtocol,
        HttpRequest request,
        HttpResponse response)
    {
        var started = Stopwatch.GetTimestamp();
        var payload = await _bodyReader.ReadJsonObjectAsync(request, request.HttpContext.RequestAborted);
        if (entryProtocol == ProtocolConverter.Responses && payload is not null)
        {
            var services = request.HttpContext.RequestServices;
            if (services?.GetService<MultiAgentResponseService>() is { } clientAgents)
            {
                // Bound child threads keep their server context even when their selected model is not simulated.
                var boundResponse = await clientAgents.TryClientResponsesAsync(payload, allowCreate: false);
                if (boundResponse is not null) return boundResponse;
            }
        }
        if (entryProtocol == ProtocolConverter.Responses && payload is not null
            && _catalog.SimulatesMultiAgent(JsonDictionaryValue.String(payload, "model"))
            && !await IsNativeResponsesPassthroughAsync(payload))
        {
            return await request.HttpContext.RequestServices.GetRequiredService<MultiAgentResponseService>().Responses(payload);
        }
        var probeRequestId = Guid.NewGuid().ToString();
        if (payload is not null
            && _proxySettings.GetBool("intercept_probe_requests", false)
            && ProbeRequestInterceptor.TryIntercept(
                entryProtocol,
                payload,
                probeRequestId,
                out var probeResult))
        {
            var identity = _identity.RequireIdentity();
            var requestMetadata = ProxyRequestMetadataFactory.FromHttpRequest(
                request,
                request.HttpContext.Connection.RemoteIpAddress?.ToString());
            var responsePayload = probeResult!.Payload as Dictionary<string, object?>;
            await _logs.WriteLogAsync(
                new ProxyLogContext(
                    probeRequestId,
                    identity.OwnerUsername,
                    identity.ApiKeyId,
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

    /// <summary>
    /// responses 接口由 OpenAI 原生支持，且只有 OpenAI 模型能完整使用 v2，
    /// 因此 responses -> responses 直通时命中 v2 模拟的模型也取消注入，保持透传。
    /// 判定使用按优先级排序的首个路由候选渠道；亲和、容量与熔断导致的渠道切换仍由普通管线处理。
    /// </summary>
    private async Task<bool> IsNativeResponsesPassthroughAsync(Dictionary<string, object?> payload)
    {
        var model = JsonDictionaryValue.String(payload, "model");
        if (model.Length == 0)
        {
            return false;
        }

        IReadOnlyList<ProxyRouteDto> routeCandidates;
        try
        {
            var identity = _identity.RequireIdentity();
            routeCandidates = await _routes.ListRouteCandidatesAsync(identity.OwnerUsername, model);
        }
        catch (RoutingException)
        {
            // 路由失败保持原有行为：继续交给多代理运行器或普通管线报告错误。
            return false;
        }

        if (routeCandidates.Count == 0)
        {
            return false;
        }

        var channelType = JsonDictionaryValue.String(routeCandidates[0].Channel, "type");
        return MultiAgentV2Policy.IsNativeResponsesPassthrough(
            ProtocolConverter.Responses,
            channelType);
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
