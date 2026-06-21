using Microsoft.AspNetCore.Mvc;
using OpenCodex.CoreBase.DTOs.ApiKeys;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Api.Controllers;

public sealed class ApiKeysController : AuthenticatedApiControllerBase
{
    private readonly IApiKeyService _apiKeys;

    public ApiKeysController(
        IWorkContext workContext,
        IApiKeyService apiKeys)
        : base(workContext)
    {
        _apiKeys = apiKeys;
    }

    [HttpGet("/api-keys")]
    public IActionResult ApiKeys(string? owner_username)
    {
        RequireUser();
        var result = _apiKeys.ListKeys(owner_username);

        return Api(result);
    }

    [HttpPost("/api-keys")]
    public IActionResult CreateApiKey(ApiKeyCreateRequest request)
    {
        RequireUser();
        var result = _apiKeys.CreateKey(request.ToCommand());
        return Api(result, StatusCodes.Status201Created);
    }

    [HttpPatch("/api-keys/{keyId:guid}")]
    public IActionResult UpdateApiKey(Guid keyId, ApiKeyUpdateRequest request)
    {
        RequireUser();
        var result = _apiKeys.UpdateKey(keyId, request.ToCommand());
        return Api(result);
    }

    [HttpDelete("/api-keys/{keyId:guid}")]
    public IActionResult DeleteApiKey(Guid keyId)
    {
        RequireUser();
        var result = _apiKeys.DeleteKey(keyId);
        return Api(result);
    }
}
