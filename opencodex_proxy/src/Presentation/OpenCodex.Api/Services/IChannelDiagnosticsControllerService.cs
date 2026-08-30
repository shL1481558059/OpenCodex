using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.ChannelDiagnostics;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.Api.Services;

/// <summary>
/// 渠道诊断服务：模型发现与流式通道测试。
/// </summary>
public interface IChannelDiagnosticsControllerService
{
    Task<ApiOpResult<DiscoverModelsResponse>> DiscoverModelsAsync(
        ChannelDiscoverRequest request,
        CancellationToken cancellationToken);

    Task StreamTestChannelAsync(
        ChannelTestRequest request,
        SessionUser user,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CancellationToken cancellationToken);
}
