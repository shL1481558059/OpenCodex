using OpenCodex.Api.Domain;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public interface IAdminApiKeyService
{
    ServiceResult<IReadOnlyList<AccessApiKeyRecord>> ListKeys(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<AccessApiKeyRecord> CreateKey(
        AdminApiKeyCreateCommand command,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult<AccessApiKeyRecord> UpdateKey(
        long keyId,
        AdminApiKeyUpdateCommand command,
        string currentUsername,
        bool isSuperadmin);

    ServiceResult DeleteKey(
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
