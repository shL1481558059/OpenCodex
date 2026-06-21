using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.Domain;
using OpenCodex.CoreBase.DTOs;
using OpenCodex.CoreBase.DTOs.Pricing;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class PricingController : AuthenticatedApiControllerBase
{
    private readonly IModelPricingService _pricing;

    public PricingController(
        IWorkContext workContext,
        IModelPricingService pricing)
        : base(workContext)
    {
        _pricing = pricing;
    }

    [HttpGet("/pricing")]
    public IActionResult Prices(
        [FromQuery] string? query,
        [FromQuery] string? vendor,
        [FromQuery] bool? enabled)
    {
        RequireSuperadmin();
        var result = _pricing.ListPrices(query, vendor, enabled);
        return Api(result);
    }

    [HttpPost("/pricing")]
    public IActionResult CreatePrice(ModelPricingCreateRequest request)
    {
        RequireSuperadmin();
        var result = _pricing.CreatePrice(request.ToCommand());
        return Api(result, StatusCodes.Status201Created);
    }

    [HttpPatch("/pricing/{id:guid}")]
    public IActionResult UpdatePrice(
        Guid id,
        Dictionary<string, object?> request)
    {
        RequireSuperadmin();
        var result = _pricing.UpdatePrice(
            id,
            new ModelPricingUpdateCommand(JsonRequestValue.Object(request)));
        return Api(result);
    }

    [HttpDelete("/pricing/{id:guid}")]
    public IActionResult DeletePrice(Guid id)
    {
        RequireSuperadmin();
        var result = _pricing.DeletePrice(id);
        return Api(result);
    }

    [HttpPost("/pricing/seed-defaults")]
    public async Task<IActionResult> SeedDefaults(CancellationToken cancellationToken)
    {
        RequireSuperadmin();
        var result = await _pricing.SeedDefaultsAsync(cancellationToken);
        return Api(result);
    }
}
