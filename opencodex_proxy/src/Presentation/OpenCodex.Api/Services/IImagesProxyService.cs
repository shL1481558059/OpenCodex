using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Services;

/// <summary>
/// 图片生成 / 图片编辑代理端点服务。
/// </summary>
public interface IImagesProxyService
{
    Task<IActionResult> GenerationsAsync(HttpRequest request, HttpResponse response);

    Task<IActionResult> EditsAsync(HttpRequest request, HttpResponse response);
}
