using OpenCodex.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace OpenCodex.Api.Controllers;

public sealed class ImagesController : ApiControllerBase
{
    private readonly IImagesProxyService _images;

    public ImagesController(
        IImagesProxyService images)
    {
        _images = images;
    }

    [HttpPost("/images/generations")]
    [HttpPost("/v1/images/generations")]
    public Task<IActionResult> Generations()
    {
        return _images.GenerationsAsync(Request, Response);
    }

    [HttpPost("/images/edits")]
    [HttpPost("/v1/images/edits")]
    public Task<IActionResult> Edits()
    {
        return _images.EditsAsync(Request, Response);
    }
}
