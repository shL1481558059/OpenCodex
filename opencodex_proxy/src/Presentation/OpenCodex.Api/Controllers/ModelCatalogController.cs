using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs.Models;
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

    [HttpPost("/model-infos/seed-defaults")]
    public IActionResult SeedDefaults()
    {
        RequireSuperadmin();
        return Api(_catalog.SeedDefaults());
    }
}
