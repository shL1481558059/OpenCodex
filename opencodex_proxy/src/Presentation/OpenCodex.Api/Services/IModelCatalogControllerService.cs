using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Services;

/// <summary>
/// 模型目录管理服务（导出等管理端操作）。
/// </summary>
public interface IModelCatalogControllerService
{
    IActionResult ExportCatalog(HttpResponse response);
}
