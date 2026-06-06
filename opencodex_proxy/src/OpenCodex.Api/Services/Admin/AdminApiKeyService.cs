using OpenCodex.Api.Domain;
using OpenCodex.Api.Persistence;
using OpenCodex.Api.Services.Results;

namespace OpenCodex.Api.Services;

public sealed class AdminApiKeyService : IAdminApiKeyService
{
    private readonly IAdminApiKeyRepository _apiKeys;

    public AdminApiKeyService(IAdminApiKeyRepository apiKeys)
    {
        _apiKeys = apiKeys;
    }

    public ServiceResult<IReadOnlyList<AccessApiKeyRecord>> ListKeys(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin)
    {
        return ServiceResult.Success(_apiKeys.ListAccessApiKeys(OwnerScope(
            requestedOwnerUsername,
            currentUsername,
            isSuperadmin)));
    }

    public ServiceResult<AccessApiKeyRecord> CreateKey(
        AdminApiKeyCreateCommand command,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            var owner = command.OwnerUsername.Trim();
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = currentUsername;
            }

            if (!isSuperadmin)
            {
                owner = currentUsername;
            }

            return ServiceResult.Success(_apiKeys.CreateAccessApiKey(
                owner,
                command.Name.Trim()));
        }
        catch (ArgumentException exception)
        {
            return ValidationFailure(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationFailure(exception.Message);
        }
    }

    public ServiceResult<AccessApiKeyRecord> UpdateKey(
        long keyId,
        AdminApiKeyUpdateCommand command,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            return ServiceResult.Success(_apiKeys.SetAccessApiKeyEnabled(
                keyId,
                command.Enabled,
                OwnerScope(currentUsername, isSuperadmin)));
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult.Fail<AccessApiKeyRecord>(AdminApiKeyErrorCodes.NotFound, exception.Message);
        }
    }

    public ServiceResult DeleteKey(
        long keyId,
        string currentUsername,
        bool isSuperadmin)
    {
        try
        {
            _apiKeys.DeleteAccessApiKey(keyId, OwnerScope(currentUsername, isSuperadmin));
            return ServiceResult.Success();
        }
        catch (InvalidOperationException exception)
        {
            return ServiceResult.Fail(AdminApiKeyErrorCodes.NotFound, exception.Message);
        }
    }

    private static string? OwnerScope(
        string? requestedOwnerUsername,
        string currentUsername,
        bool isSuperadmin)
    {
        if (!isSuperadmin)
        {
            return currentUsername;
        }

        return string.IsNullOrWhiteSpace(requestedOwnerUsername)
            ? null
            : requestedOwnerUsername.Trim();
    }

    private static string? OwnerScope(string currentUsername, bool isSuperadmin)
    {
        return isSuperadmin ? null : currentUsername;
    }

    private static ServiceResult<AccessApiKeyRecord> ValidationFailure(string message)
    {
        return ServiceResult.Fail<AccessApiKeyRecord>(AdminApiKeyErrorCodes.Validation, message);
    }
}
