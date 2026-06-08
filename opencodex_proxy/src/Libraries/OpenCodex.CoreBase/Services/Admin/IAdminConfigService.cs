using OpenCodex.CoreBase.DTOs.AdminConfig;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminConfigService
{
    ApiResult<ConfigResponse> ReadConfig(
        string currentUsername,
        bool isSuperadmin);

    ApiResult<ConfigResponse> SaveConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<ConfigImportResponse> ImportConfig(
        IReadOnlyDictionary<string, object?> body,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<ConfigExportResponse> ExportConfig(
        string currentUsername,
        bool isSuperadmin);
}
