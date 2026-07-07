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
    public async Task<IActionResult> UpdateApiKey(Guid keyId, ApiKeyUpdateRequest request)
    {
        RequireUser();
        var result = await _apiKeys.UpdateKeyAsync(keyId, request.ToCommand());
        return Api(result);
    }

    [HttpDelete("/api-keys/{keyId:guid}")]
    public async Task<IActionResult> DeleteApiKey(Guid keyId)
    {
        RequireUser();
        var result = await _apiKeys.DeleteKeyAsync(keyId);
        return Api(result);
    }

    [HttpPost("/api-keys/import")]
    public async Task<IActionResult> ImportApiKeys(ApiKeyImportRequest request)
    {
        RequireUser();
        var result = await _apiKeys.ImportKeysAsync(request.ToCommand());
        return Api(result);
    }
}
