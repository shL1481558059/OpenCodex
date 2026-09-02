using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Controllers;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Api.Services;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Models;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.DTOs.SystemSettings;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ProxyControllerTests
{
    [Fact]
    public async Task Messages_EnabledProbeInterception_ReturnsFakeResponseWithoutProxy()
    {
        var proxy = new StubProxyEndpointService();
        var logs = new StubProxyLogService();
        var controller = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 1)),
            proxy,
            interceptProbeRequests: true,
            logs: logs);

        var action = await controller.Messages();

        var objectResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(200, objectResult.StatusCode);
        var payload = Assert.IsType<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal("message", payload["type"]);
        Assert.Equal("end_turn", payload["stop_reason"]);
        Assert.False(proxy.Called);
        Assert.NotNull(logs.LastContext);
        Assert.Equal(200, logs.LastContext!.StatusCode);
        Assert.Equal("claude-opus-5", logs.LastContext.RequestModel);
        Assert.Same(payload, logs.LastContext.ResponsePayload);
        Assert.NotNull(logs.LastRequestMetadata);
        Assert.Equal("POST", logs.LastRequestMetadata!.Method);
        Assert.Equal("/v1/messages", logs.LastRequestMetadata.Path);
        Assert.Equal("{\"model\":\"claude-opus-5\",\"max_tokens\":1}", logs.LastRequestMetadata.RawBody);
    }

    [Fact]
    public async Task Messages_DisabledProbeInterception_ForwardsToProxy()
    {
        var proxy = new StubProxyEndpointService();
        var controller = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 1)),
            proxy,
            interceptProbeRequests: false);

        var action = await controller.Messages();

        var objectResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(200, objectResult.StatusCode);
        Assert.True(proxy.Called);
        var payload = Assert.IsType<Dictionary<string, object?>>(objectResult.Value);
        Assert.Equal("forwarded", payload["routed"]);
    }

    [Fact]
    public async Task Messages_EnabledDoesNotInterceptNormalRequest()
    {
        var proxy = new StubProxyEndpointService();
        var controller = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 4096)),
            proxy,
            interceptProbeRequests: true);

        var action = await controller.Messages();

        var objectResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(200, objectResult.StatusCode);
        Assert.True(proxy.Called);
    }

    [Fact]
    public async Task Models_CodexClient_GetsGptTemplatesMergedWithCatalog()
    {
        var catalogModels = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["slug"] = "gpt-5.5",
                ["display_name"] = "GPT-5.5 (catalog)"
            },
            new()
            {
                ["slug"] = "claude-opus-5",
                ["display_name"] = "Claude Opus 5"
            }
        };
        var gptTemplateModels = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["slug"] = "gpt-5.5",
                ["display_name"] = "GPT-5.5 (template)",
                ["context_window"] = 1000000
            }
        };
        var codexModels = new StubCodexOfficialModelCatalogService(gptTemplateModels);

        var codexController = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 4096)),
            new StubProxyEndpointService(),
            interceptProbeRequests: false,
            modelCatalog: catalogModels,
            codexModels: codexModels);
        codexController.HttpContext.Request.QueryString = QueryString.Create("client_version", "0.147.0");

        var codexAction = await codexController.Models();
        var codexResult = Assert.IsType<ObjectResult>(codexAction);
        Assert.Equal(200, codexResult.StatusCode);
        var codexPayload = Assert.IsType<Dictionary<string, object?>>(codexResult.Value);
        Assert.Equal(1, codexPayload.Count);
        Assert.True(codexPayload.ContainsKey("models"));
        Assert.False(codexPayload.ContainsKey("object"));
        Assert.False(codexPayload.ContainsKey("data"));

        var codexModelsList = Assert.IsType<List<Dictionary<string, object?>>>(codexPayload["models"]);
        var gpt55 = Assert.Single(codexModelsList, m =>
        {
            Assert.NotNull(m["slug"]);
            return "gpt-5.5".Equals(m["slug"]);
        });
        Assert.Equal("GPT-5.5 (template)", gpt55["display_name"]);
        Assert.Equal(1000000, gpt55["context_window"]);
        var claude = Assert.Single(codexModelsList, m =>
        {
            Assert.NotNull(m["slug"]);
            return "claude-opus-5".Equals(m["slug"]);
        });
        Assert.Equal("Claude Opus 5", claude["display_name"]);

        var regularController = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 4096)),
            new StubProxyEndpointService(),
            interceptProbeRequests: false,
            modelCatalog: catalogModels,
            codexModels: codexModels);
        var regularAction = await regularController.Models();
        var regularResult = Assert.IsType<ObjectResult>(regularAction);
        Assert.Equal(200, regularResult.StatusCode);
        var regularPayload = Assert.IsType<Dictionary<string, object?>>(regularResult.Value);
        Assert.True(regularPayload.ContainsKey("object"));
        Assert.True(regularPayload.ContainsKey("data"));
        Assert.True(regularPayload.ContainsKey("models"));
    }

    private static ProxyController CreateController(
        IRequestBodyReader bodyReader,
        StubProxyEndpointService proxy,
        bool interceptProbeRequests,
        IReadOnlyList<Dictionary<string, object?>>? modelCatalog = null,
        ICodexOfficialModelCatalogService? codexModels = null,
        IProxyLogService? logs = null)
    {
        var proxyService = new ProxyService(
            bodyReader,
            proxy,
            new StubProxyRequestService(),
            new StubProxyRouteService(),
            new StubModelCatalogService(modelCatalog),
            codexModels ?? new StubCodexOfficialModelCatalogService(),
            new StubProxySettingsService(interceptProbeRequests),
            logs ?? new StubProxyLogService());
        var controller = new ProxyController(proxyService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.Path = "/v1/messages";
        return controller;
    }

    private sealed class StubCodexOfficialModelCatalogService : ICodexOfficialModelCatalogService
    {
        private readonly IReadOnlyList<Dictionary<string, object?>> _models;

        public StubCodexOfficialModelCatalogService(IReadOnlyList<Dictionary<string, object?>>? models = null)
        {
            _models = models ?? [];
        }

        public IReadOnlyList<Dictionary<string, object?>> BuildCodexGptModels()
        {
            return _models;
        }
    }

    private static Dictionary<string, object?> CreateMessagesPayload(int maxTokens)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "claude-opus-5",
            ["max_tokens"] = maxTokens,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "ping"
                }
            }
        };
    }

    private sealed class StubRequestBodyReader : IRequestBodyReader
    {
        private readonly Dictionary<string, object?> _payload;

        public StubRequestBodyReader(Dictionary<string, object?> payload)
        {
            _payload = payload;
        }

        public Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
            HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            request.Body = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"model\":\"claude-opus-5\",\"max_tokens\":1}"));
            return ReadAndReturnPayloadAsync(request, cancellationToken);
        }

        private async Task<Dictionary<string, object?>?> ReadAndReturnPayloadAsync(
            HttpRequest request,
            CancellationToken cancellationToken)
        {
            await new RequestBodyReader().ReadJsonObjectAsync(request, cancellationToken);
            return _payload;
        }
    }

    private sealed class StubProxyEndpointService : IProxyEndpointService
    {
        public bool Called { get; private set; }

        public Task<ProxyEndpointResult> ProxyAsync(ProxyEndpointContext context)
        {
            Called = true;
            return Task.FromResult(new ProxyEndpointResult(
                200,
                new Dictionary<string, object?>
                {
                    ["routed"] = "forwarded"
                },
                IsEmpty: false));
        }
    }

    private sealed class StubProxyLogService : IProxyLogService
    {
        public ProxyLogContext? LastContext { get; private set; }

        public ProxyRequestMetadata? LastRequestMetadata { get; private set; }

        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context) => throw new NotSupportedException();

        public void MarkProcessing(Guid requestLogId, ProxyRequestLogProcessingContext context) => throw new NotSupportedException();

        public Task CompleteLogAsync(Guid requestLogId, ProxyLogContext context, ProxyRequestMetadata request)
            => throw new NotSupportedException();

        public Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request)
        {
            LastContext = context;
            LastRequestMetadata = request;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context) => throw new NotSupportedException();
    }

    private sealed class StubProxyRequestService : IProxyRequestService
    {
        public ProxyRequestState StartRequest()
        {
            return new ProxyRequestState("req-1", "admin", 120);
        }

        public Task<AuthenticatedAccessApiKeyDto> AuthenticateAccessKeyAsync(string? authorizationHeader)
        {
            return Task.FromResult(new AuthenticatedAccessApiKeyDto(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "admin",
                "test",
                "sk-test",
                "suffix",
                "sk-***",
                true,
                0,
                0,
                null,
                new AccessApiKeyUserDto(Guid.NewGuid(), "admin", "superadmin", true)));
        }
    }

    private sealed class StubProxyRouteService : IProxyRouteService
    {
        public Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
            string ownerUsername,
            string? model)
        {
            return Task.FromResult<IReadOnlyList<ProxyRouteDto>>([]);
        }

        public Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string ownerUsername)
        {
            return Task.FromResult(VisionTransferRoutesDto.NotConfigured());
        }

        public Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername)
        {
            return Task.FromResult<IReadOnlyList<ProxyModelCapabilityDto>>([]);
        }
    }

    private sealed class StubModelCatalogService : IModelCatalogService
    {
        private readonly IReadOnlyList<Dictionary<string, object?>> _modelCatalog;

        public StubModelCatalogService(IReadOnlyList<Dictionary<string, object?>>? modelCatalog = null)
        {
            _modelCatalog = modelCatalog ?? [];
        }

        public ApiOpResult<ModelProviderListResponse> ListProviders(bool includeDisabled = false)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelProviderResponsePayload> CreateProvider(ModelProviderUpsertRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelProviderResponsePayload> UpdateProvider(Guid id, ModelProviderUpsertRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelProviderResponsePayload> DeleteProvider(Guid id)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelInfoListResponse> ListModels(string? query, string? providerCode, bool? enabled)
        {
            return ApiOpResult<ModelInfoListResponse>.Succeed(new ModelInfoListResponse([]));
        }

        public IReadOnlyList<Dictionary<string, object?>> BuildProxyModelCatalog(
            IReadOnlyList<ProxyModelCapabilityDto> routedModels)
        {
            return _modelCatalog;
        }

        public ApiOpResult<ModelInfoResponsePayload> ReadModelInfoById(Guid id)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelInfoResponsePayload> CreateModel(ModelInfoCreateRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelInfoResponsePayload> UpdateModel(Guid id, ModelInfoUpdateRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelInfoResponsePayload> DeleteModel(Guid id)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelBatchActionResult> BatchModels(ModelBatchActionRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelCatalogTransferDocument> ExportModelCatalog()
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(
            ModelCatalogTransferDocument document,
            bool dryRun)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ModelCatalogImportResult> ImportModelCatalog(
            ModelCatalogTransferDocument document,
            bool dryRun,
            ModelCatalogImportOptions options)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ChannelModelInfoListResponse> ListChannelModelInfos(Guid channelId)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult<ChannelModelInfoResponsePayload> UpsertChannelModelInfo(
            Guid channelId,
            ChannelModelInfoUpsertRequest request)
        {
            throw new NotSupportedException();
        }

        public ApiOpResult DeleteChannelModelInfo(Guid channelId, Guid id)
        {
            throw new NotSupportedException();
        }

        public bool SupportsImage(Guid? channelId, string? upstreamModel)
        {
            throw new NotSupportedException();
        }

       public Task<ModelPricingCalculationResult> CalculateCostAsync(
           Guid? channelId,
           string? requestModel,
           string? upstreamModel,
           ModelUsageVector usage,
           DateTimeOffset billingInstant)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubProxySettingsService : IProxySettingsService
    {
        private readonly bool _interceptProbeRequests;

        public StubProxySettingsService(bool interceptProbeRequests)
        {
            _interceptProbeRequests = interceptProbeRequests;
        }

        public bool GetBool(string key, bool fallback)
        {
            return key == "intercept_probe_requests" ? _interceptProbeRequests : fallback;
        }

        public decimal GetDecimal(string key, decimal fallback)
        {
            return fallback;
        }

        public Task<ApiOpResult<Dictionary<string, string>>> GetAllAsync()
        {
            return Task.FromResult(ApiOpResult<Dictionary<string, string>>.Succeed(
                new Dictionary<string, string>
                {
                    ["intercept_probe_requests"] = _interceptProbeRequests.ToString()
                }));
        }

        public Task<ApiOpResult> SetAsync(string key, string value)
        {
            return Task.FromResult(ApiOpResult.Succeed());
        }
    }
}
