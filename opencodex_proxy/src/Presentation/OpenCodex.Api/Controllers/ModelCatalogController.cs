using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ModelCatalogController : AuthenticatedApiControllerBase
{
    private readonly IModelCatalogService _catalog;

    public ModelCatalogController(
        IWorkContext workContext,
        IModelCatalogService catalog)
        : base(workContext)
    {
        _catalog = catalog;
    }

    [HttpGet("/model-providers")]
    public IActionResult Providers([FromQuery] bool includeDisabled = false)
    {
        RequireUser();
        return Api(_catalog.ListProviders(includeDisabled));
    }

    [HttpPost("/model-providers")]
    public IActionResult CreateProvider(ModelProviderUpsertRequest request)
    {
        RequireSuperadmin();
        return Api(_catalog.CreateProvider(request), StatusCodes.Status201Created);
    }

    [HttpGet("/model-infos")]
    public IActionResult Models(
        [FromQuery] string? query,
        [FromQuery] string? provider,
        [FromQuery] bool? enabled)
    {
        RequireUser();
        return Api(_catalog.ListModels(query, provider, enabled));
    }

    [HttpPost("/model-infos")]
    public IActionResult CreateModel(ModelInfoCreateRequest request)
    {
        RequireSuperadmin();
        return Api(_catalog.CreateModel(request), StatusCodes.Status201Created);
    }

    [HttpPatch("/model-infos/{id:guid}")]
    public IActionResult UpdateModel(Guid id, ModelInfoUpdateRequest request)
    {
        RequireSuperadmin();
        return Api(_catalog.UpdateModel(id, request));
    }

    [HttpDelete("/model-infos/{id:guid}")]
    public IActionResult DeleteModel(Guid id)
    {
        RequireSuperadmin();
        return Api(_catalog.DeleteModel(id));
    }

    [HttpGet("/model-catalog/export")]
    public IActionResult ExportCatalog()
    {
        RequireSuperadmin();
        var result = _catalog.ExportModelCatalog();
        if (!result.Succeeded)
        {
            return StatusCode(result.Code, result);
        }

        Response.ContentType = "application/json";
        return File(
            JsonSerializer.SerializeToUtf8Bytes(result.Payload!),
            "application/json",
            $"model-catalog-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpPost("/model-catalog/import")]
    public IActionResult ImportCatalog([FromQuery] string? dryRun, ModelCatalogTransferDocument request)
    {
        RequireSuperadmin();
        if (!bool.TryParse(dryRun, out var parsedDryRun))
        {
            return Api(ApiOpResult<ModelCatalogImportResult>.Fail(400, "dryRun must be true or false"));
        }

        return Api(_catalog.ImportModelCatalog(request, parsedDryRun));
    }

    [HttpGet("/channels/{channelId:guid}/model-infos")]
    public IActionResult ChannelModels(Guid channelId)
    {
        RequireUser();
        return Api(_catalog.ListChannelModelInfos(channelId));
    }

    [HttpPut("/channels/{channelId:guid}/model-infos")]
    public IActionResult UpsertChannelModel(Guid channelId, ChannelModelInfoUpsertRequest request)
    {
        RequireUser();
        return Api(_catalog.UpsertChannelModelInfo(channelId, request));
    }

    [HttpDelete("/channels/{channelId:guid}/model-infos/{id:guid}")]
    public IActionResult RestoreChannelModel(Guid channelId, Guid id)
    {
        RequireUser();
        return Api(_catalog.RestoreChannelModelInfo(channelId, id));
    }

}
