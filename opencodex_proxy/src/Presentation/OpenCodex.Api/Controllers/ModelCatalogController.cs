using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;
using OpenCodex.Api.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ModelCatalogController : AuthenticatedApiControllerBase
{
    private readonly IModelCatalogService _catalog;
    private readonly IModelCatalogSyncService _syncService;
    private readonly IModelCatalogControllerService _catalogController;

    public ModelCatalogController(
        IWorkContext workContext,
        IModelCatalogService catalog,
        IModelCatalogSyncService syncService,
        IModelCatalogControllerService catalogController)
        : base(workContext)
    {
        _catalog = catalog;
        _syncService = syncService;
        _catalogController = catalogController;
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

    [HttpPatch("/model-providers/{id:guid}")]
    public IActionResult UpdateProvider(Guid id, ModelProviderUpsertRequest request)
    {
        RequireSuperadmin();
        return Api(_catalog.UpdateProvider(id, request));
    }

    [HttpDelete("/model-providers/{id:guid}")]
    public IActionResult DeleteProvider(Guid id)
    {
        RequireSuperadmin();
        return Api(_catalog.DeleteProvider(id));
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

    [HttpGet("/model-infos/{id:guid}")]
    public IActionResult ModelInfo(Guid id)
    {
        RequireUser();
        return Api(_catalog.ReadModelInfoById(id));
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

    [HttpPost("/model-infos/batch")]
    public IActionResult BatchModels(ModelBatchActionRequest request)
    {
        RequireSuperadmin();
        return Api(_catalog.BatchModels(request));
    }

    [HttpGet("/model-catalog/export")]
    public IActionResult ExportCatalog()
    {
        RequireSuperadmin();
        return _catalogController.ExportCatalog(Response);
    }

    [HttpPost("/model-catalog/import")]
    public IActionResult ImportCatalog(ModelCatalogTransferDocument request, [FromQuery] bool dryRun = false)
    {
        RequireSuperadmin();
        return Api(_catalog.ImportModelCatalog(request, dryRun));
    }

    [HttpPost("/model-catalog/sync")]
    public async Task<IActionResult> SyncCatalog(
        [FromQuery] string mode = "incremental",
        [FromQuery] bool dryRun = true)
    {
        RequireSuperadmin();
        var result = await _syncService.SyncAsync(mode, dryRun);
        return Api(result);
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
    public IActionResult DeleteChannelModel(Guid channelId, Guid id)
    {
        RequireUser();
        return Api(_catalog.DeleteChannelModelInfo(channelId, id));
    }

}
