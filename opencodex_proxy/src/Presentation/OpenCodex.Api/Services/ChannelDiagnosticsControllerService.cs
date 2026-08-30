using Microsoft.AspNetCore.Mvc;
using OpenCodex.Api.Infrastructure;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 渠道诊断实现：模型发现与流式通道测试。
/// </summary>
public sealed class ChannelDiagnosticsControllerService : IChannelDiagnosticsControllerService
{
    private readonly IChannelDiagnosticsService _channelDiagnostics;

    public ChannelDiagnosticsControllerService(IChannelDiagnosticsService channelDiagnostics)
    {
        _channelDiagnostics = channelDiagnostics;
    }

    public Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        ChannelDiscoverRequest request,
        CancellationToken cancellationToken)
        => _channelDiagnostics.DiscoverModelsAsync(request.ToDictionary(), cancellationToken);

    public Task StreamTestChannelAsync(
        ChannelTestRequest request,
        SessionUser user,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CancellationToken cancellationToken)
        => _channelDiagnostics.StreamTestChannelAsync(
            request.ToDictionary(),
            user,
            ProxyRequestMetadataFactory.FromHttpRequest(
                httpRequest,
                httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString()),
            new ProxyStreamResponseWriter(httpResponse),
            cancellationToken);
}
