using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Services;

/// <summary>
/// 代理端点统一处理服务：responses / chat.completions / messages 入口转发与模型列表。
/// </summary>
public interface IProxyService
{
    Task<IActionResult> ProxyAsync(string entryProtocol, HttpRequest request, HttpResponse response);

    Task<IActionResult> ModelsAsync(HttpRequest request, HttpResponse response);
}
