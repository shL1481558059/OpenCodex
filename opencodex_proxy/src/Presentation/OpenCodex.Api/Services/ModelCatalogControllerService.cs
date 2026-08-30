using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Services;

/// <summary>
/// 模型目录管理实现：导出模型目录。
/// </summary>
public sealed class ModelCatalogControllerService : IModelCatalogControllerService
{
    private readonly IModelCatalogService _catalog;

    public ModelCatalogControllerService(IModelCatalogService catalog)
    {
        _catalog = catalog;
    }

    public IActionResult ExportCatalog(HttpResponse response)
    {
        var result = _catalog.ExportModelCatalog();
        if (!result.Succeeded)
        {
            return new ObjectResult(result) { StatusCode = result.Code };
        }

        if (result.Payload is null)
        {
            return new ObjectResult(ApiOpResult.Fail(500, "export payload is empty"))
            {
                StatusCode = 500
            };
        }

        response.ContentType = "application/json";
        return new FileContentResult(
            JsonSerializer.SerializeToUtf8Bytes(result.Payload),
            "application/json")
        {
            FileDownloadName = $"model-catalog-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"
        };
    }
}
