namespace OpenCodex.Api.Services;

public interface ICodexOfficialModelCatalogService
{
    IReadOnlyList<Dictionary<string, object?>> BuildCodexGptModels();
}
