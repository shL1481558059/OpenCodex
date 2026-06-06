using System.Text.Json.Serialization;
using OpenCodex.Api.Domain;

namespace OpenCodex.Api.DTOs.AdminApiKeys;

public sealed class ApiKeysResponse
{
    public ApiKeysResponse(IReadOnlyList<AccessApiKeyResponse> keys)
    {
        Keys = keys;
    }

    [JsonPropertyName("keys")]
    public IReadOnlyList<AccessApiKeyResponse> Keys { get; }

    public static ApiKeysResponse From(IReadOnlyList<AccessApiKeyRecord>? keys)
    {
        return new ApiKeysResponse(
            keys?.Select(AccessApiKeyResponse.From).ToList() ?? []);
    }
}

public sealed class ApiKeyResponsePayload
{
    public ApiKeyResponsePayload(AccessApiKeyResponse key)
    {
        Key = key;
    }

    [JsonPropertyName("key")]
    public AccessApiKeyResponse Key { get; }

    public static ApiKeyResponsePayload From(AccessApiKeyRecord key)
    {
        return new ApiKeyResponsePayload(AccessApiKeyResponse.From(key));
    }
}

public sealed class AccessApiKeyResponse
{
    public AccessApiKeyResponse(
        long id,
        string ownerUsername,
        string name,
        string keyPrefix,
        string keySuffix,
        string maskedKey,
        bool enabled,
        double createdAt,
        double updatedAt,
        double? lastUsedAt,
        string? key)
    {
        Id = id;
        OwnerUsername = ownerUsername;
        Name = name;
        KeyPrefix = keyPrefix;
        KeySuffix = keySuffix;
        MaskedKey = maskedKey;
        Enabled = enabled;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastUsedAt = lastUsedAt;
        Key = key;
    }

    [JsonPropertyName("id")]
    public long Id { get; }

    [JsonPropertyName("owner_username")]
    public string OwnerUsername { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("key_prefix")]
    public string KeyPrefix { get; }

    [JsonPropertyName("key_suffix")]
    public string KeySuffix { get; }

    [JsonPropertyName("masked_key")]
    public string MaskedKey { get; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; }

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; }

    [JsonPropertyName("last_used_at")]
    public double? LastUsedAt { get; }

    [JsonPropertyName("key")]
    public string? Key { get; }

    public static AccessApiKeyResponse From(AccessApiKeyRecord key)
    {
        return new AccessApiKeyResponse(
            key.Id,
            key.OwnerUsername,
            key.Name,
            key.KeyPrefix,
            key.KeySuffix,
            key.MaskedKey,
            key.Enabled,
            key.CreatedAt,
            key.UpdatedAt,
            key.LastUsedAt,
            key.Key);
    }
}

public sealed class DeleteApiKeyResponse
{
    public DeleteApiKeyResponse(bool deleted)
    {
        Deleted = deleted;
    }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; }
}
