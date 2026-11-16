namespace OpenCodex.Core.Services.MultiAgent;

public sealed class NativeClientCacheOptions
{
    public int SessionTtlSeconds { get; set; } = 900;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int MaxSessions { get; set; } = 4096;
    public int MaxTenantSessions { get; set; } = 512;
    public int MaxGroupSessions { get; set; } = 256;
    public int MaxWaitersPerSession { get; set; } = 128;
    public int MaxIdentityCharacters { get; set; } = 512;

    internal NativeClientCacheOptions ValidatedCopy()
    {
        var copy = (NativeClientCacheOptions)MemberwiseClone();
        if (copy.SessionTtlSeconds <= 0 || copy.CleanupIntervalSeconds <= 0
            || copy.MaxSessions <= 0 || copy.MaxTenantSessions <= 0 || copy.MaxGroupSessions <= 0 || copy.MaxWaitersPerSession <= 0
            || copy.MaxIdentityCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(NativeClientCacheOptions));
        return copy;
    }
}
