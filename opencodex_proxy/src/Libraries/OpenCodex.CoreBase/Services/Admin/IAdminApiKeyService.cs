using OpenCodex.CoreBase.DTOs.AdminApiKeys;
using OpenCodex.CoreBase.Results;

namespace OpenCodex.CoreBase.Services.Admin;

public interface IAdminApiKeyService
{
    ApiResult<ApiKeysResponse> ListKeys(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<ApiKeyResponsePayload> CreateKey(
        AdminApiKeyCreateCommand command,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<ApiKeyResponsePayload> UpdateKey(
        long keyId,
        AdminApiKeyUpdateCommand command,
        string currentUsername,
        bool isSuperadmin);

    ApiResult<DeleteApiKeyResponse> DeleteKey(
        long keyId,
        string currentUsername,
        bool isSuperadmin);
}

public sealed class AdminApiKeyCreateCommand
{
    public AdminApiKeyCreateCommand(string ownerUsername, string name)
    {
        OwnerUsername = ownerUsername;
        Name = name;
    }

    public string OwnerUsername { get; }

    public string Name { get; }
}

public sealed class AdminApiKeyUpdateCommand
{
    public AdminApiKeyUpdateCommand(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }
}
