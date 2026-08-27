using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Configuration;
using OpenCodex.Api.Controllers;
using OpenCodex.Api.Infrastructure;
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
    public async Task Models_CodexClientHeader_ReturnsModelsPayload()
    {
        var expectedModels = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["slug"] = "gpt-5.5"
            }
        };
        var factory = new StubCodexOfficialModelCatalogFactory(expectedModels);
        var controller = CreateController(
            new StubRequestBodyReader(CreateMessagesPayload(maxTokens: 4096)),
            new StubProxyEndpointService(),
            interceptProbeRequests: false,
            codexFactory: factory);
        controller.HttpContext.Request.QueryString = QueryString.Create("client_version", "0.147.0");

        var action = await controller.Models();

        var objectResult = Assert.IsType<ObjectResult>(action);
        Assert.Equal(200, objectResult.StatusCode);
        var payload = Assert.IsType<Dictionary<string, object?>>(objectResult.Value);
        Assert.Same(expectedModels, payload["models"]);
    }

    private static ProxyController CreateController(
        IRequestBodyReader bodyReader,
        StubProxyEndpointService proxy,
        bool interceptProbeRequests,
        ICodexOfficialModelCatalogFactory? codexFactory = null,
        IProxyLogService? logs = null)
    {
        var controller = new ProxyController(
            bodyReader,
            proxy,
            new StubProxyRequestService(),
            new StubProxyRouteService(),
            new StubModelCatalogService(),
            codexFactory ?? new StubCodexOfficialModelCatalogFactory(),
            new StubSystemSettingsStore(interceptProbeRequests),
            logs ?? new StubProxyLogService())
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
        public Task<ProxyRouteDto> ChooseRouteAsync(string ownerUsername, string? model, bool requestContainsImages = false)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(
            string ownerUsername,
            string? model,
            bool requestContainsImages = false)
        {
            return Task.FromResult<IReadOnlyList<ProxyRouteDto>>([]);
        }

        public Task<ProxyRouteDto?> ChooseOcrRouteAsync(string ownerUsername, string? model)
        {
            return Task.FromResult<ProxyRouteDto?>(null);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string ownerUsername)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername)
        {
            return Task.FromResult<IReadOnlyList<ProxyModelCapabilityDto>>([]);
        }
    }

    private sealed class StubModelCatalogService : IModelCatalogService
    {
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

        public bool SupportsImage(Guid? channelId, string? upstreamModel, bool legacyMappingValue)
        {
            throw new NotSupportedException();
        }

       public Task<ModelPricingCalculationResult> CalculateCostAsync(
           Guid? channelId,
           string? requestModel,
           string? upstreamModel,
           ModelUsageVector usage)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubCodexOfficialModelCatalogFactory : ICodexOfficialModelCatalogFactory
    {
        private readonly IReadOnlyList<Dictionary<string, object?>> _result;

        public StubCodexOfficialModelCatalogFactory(IReadOnlyList<Dictionary<string, object?>>? result = null)
        {
            _result = result ?? [];
        }

        public IReadOnlyList<Dictionary<string, object?>> BuildCodexModels(
            IReadOnlyList<ProxyModelCapabilityDto> routedModels,
            IReadOnlyDictionary<string, ModelInfoResponse> catalogByModel)
        {
            return _result;
        }
    }

    private sealed class StubSystemSettingsStore : IDesktopSystemSettingsStore
    {
        private readonly bool _interceptProbeRequests;

        public StubSystemSettingsStore(bool interceptProbeRequests)
        {
            _interceptProbeRequests = interceptProbeRequests;
        }

        public SystemSettingsResponse Get()
        {
            return new SystemSettingsResponse(
                "localhost",
                "127.0.0.1",
                18080,
                false,
                false,
                _interceptProbeRequests);
        }

        public DesktopSystemSettingsDraft Normalize(SystemSettingsUpdateRequest? request)
        {
            return new DesktopSystemSettingsDraft(
                "localhost",
                "127.0.0.1",
                18080,
                _interceptProbeRequests);
        }

        public SystemSettingsResponse Save(DesktopSystemSettingsDraft draft)
        {
            return Get();
        }
    }
}
