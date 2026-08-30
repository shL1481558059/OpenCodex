using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.DTOs.Proxy;

namespace OpenCodex.Api.Services;

public interface ICodexOfficialModelCatalogService
{
    IReadOnlyList<Dictionary<string, object?>> BuildCodexModels(
        IReadOnlyList<ProxyModelCapabilityDto> routedModels,
        IReadOnlyDictionary<string, ModelInfoResponse> catalogByModel);
}
