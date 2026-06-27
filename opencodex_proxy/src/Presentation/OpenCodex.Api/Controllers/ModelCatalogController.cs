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

    [HttpGet("/model-infos")]
    public IActionResult Models(
        [FromQuery] string? query,
        [FromQuery] string? provider,
        [FromQuery] string? scope,
        [FromQuery] bool? enabled,
        [FromQuery] Guid? channelId)
    {
        RequireUser();
        return Api(_catalog.ListModels(query, provider, scope, enabled, channelId));
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

    [HttpPost("/model-infos/seed-defaults")]
    public IActionResult SeedDefaults()
    {
        RequireSuperadmin();
        return Api(_catalog.SeedDefaults());
    }
}
