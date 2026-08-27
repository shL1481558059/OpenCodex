using OpenCodex.Core.Domain;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Results;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

/// <summary>
 /// 模型目录同步服务:从远端 JSON 拉取目录并按模式执行增量或覆盖导入。
 /// </summary>
public sealed class ModelCatalogSyncService : IModelCatalogSyncService
{
    private const string DefaultSyncUrl = "https://ocxpmodel.shldev.me/model-catalog.json";

    private readonly IModelCatalogService _catalog;
    private readonly IModelCatalogSyncClient _syncClient;
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;

    public ModelCatalogSyncService(
        IModelCatalogService catalog,
        IModelCatalogSyncClient syncClient,
        IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _catalog = catalog;
        _syncClient = syncClient;
        _settingsProvider = settingsProvider;
    }

    public async Task<ApiOpResult<ModelCatalogImportResult>> SyncAsync(string mode, bool dryRun)
    {
        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode is not ("incremental" or "overwrite"))
        {
            return SyncFailure(400, $"mode must be 'incremental' or 'overwrite', got '{mode}'");
        }

        var url = ResolveSyncUrl();
        ModelCatalogTransferDocument document;
        try
        {
            document = await _syncClient.FetchAsync(url);
        }
        catch (Exception ex)
        {
            return SyncFailure(400, $"failed to fetch sync URL '{url}': {ex.Message}");
        }

        var options = BuildOptions(normalizedMode);
        var result = _catalog.ImportModelCatalog(document, dryRun, options);

        // Fill mode into the result if the service didn't already.
        if (result.Succeeded && result.Payload is not null)
        {
            // The import service sets Mode only for sync source; ensure it's always set for sync.
            // We return the result as-is since ImportModelCatalog already sets Mode.
        }

        return result;
    }

    private string ResolveSyncUrl()
    {
        var settings = _settingsProvider.GetSettings();
        var url = (settings.ModelCatalogSyncUrl ?? string.Empty).Trim();
        return url.Length == 0 ? DefaultSyncUrl : url;
    }

    private static ModelCatalogImportOptions BuildOptions(string mode)
    {
        // Both modes: skip existing providers (don't modify name/sort/enabled).
        // Both modes: preserve local enabled flag (Q21-3 and reverse).
        // Both modes: keep local pricing when remote is null.
        return new ModelCatalogImportOptions
        {
            SkipExistingModels = mode == "incremental",
            SkipExistingProviders = true,
            PreserveLocalEnabled = true,
            KeepLocalPricingWhenRemoteNull = true,
            Source = ModelCatalogSources.Sync
        };
    }

    private static ApiOpResult<ModelCatalogImportResult> SyncFailure(int code, string description)
    {
        return ApiOpResult<ModelCatalogImportResult>.Fail(code, description);
    }
}
