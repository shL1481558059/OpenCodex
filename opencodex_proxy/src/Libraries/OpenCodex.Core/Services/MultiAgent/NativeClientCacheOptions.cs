namespace OpenCodex.Core.Services.MultiAgent;

public sealed class NativeClientCacheOptions
{
    public long MaxBytes { get; set; } = 128L * 1024 * 1024;
    public long MaxTenantBytes { get; set; } = 32L * 1024 * 1024;
    public long MaxSnapshotBytes { get; set; } = 16L * 1024 * 1024;
    public int MaxResponses { get; set; } = 2048;
    public int MaxTenantResponses { get; set; } = 512;
    public int SnapshotTtlSeconds { get; set; } = 900;
    public int SessionTtlSeconds { get; set; } = 900;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int MaxSessions { get; set; } = 4096;
    public int MaxTenantSessions { get; set; } = 512;
    public int MaxGroupSessions { get; set; } = 256;
    public int MaxReplayIdentities { get; set; } = 65536;
    public int MaxTenantReplayIdentities { get; set; } = 8192;
    public int MaxSessionReplayIdentities { get; set; } = 4096;
    public int MaxWaitersPerSession { get; set; } = 128;
    public int MaxIdentityCharacters { get; set; } = 512;
    // Reserved BEFORE ephemeral identity admission, retained until process exit. Never
    // evict safety tombstones and silently recreate a turn whose tools may have executed.
    public int MaxEphemeralIdentities { get; set; } = 65536;
    public int MaxTenantEphemeralIdentities { get; set; } = 8192;
    internal NativeClientCacheOptions ValidatedCopy()
    {
        var copy = (NativeClientCacheOptions)MemberwiseClone();
        if (copy.MaxBytes <= 0 || copy.MaxBytes > long.MaxValue / 8 || copy.MaxTenantBytes <= 0 || copy.MaxTenantBytes > long.MaxValue / 8
            || copy.MaxSnapshotBytes <= 0 || copy.MaxSnapshotBytes > long.MaxValue / 8 || copy.MaxResponses <= 0 || copy.MaxTenantResponses <= 0
            || copy.SnapshotTtlSeconds <= 0 || copy.SessionTtlSeconds <= 0 || copy.CleanupIntervalSeconds <= 0
            || copy.MaxSessions <= 0 || copy.MaxTenantSessions <= 0 || copy.MaxGroupSessions <= 0 || copy.MaxWaitersPerSession <= 0
            || copy.MaxReplayIdentities <= 0 || copy.MaxTenantReplayIdentities <= 0 || copy.MaxSessionReplayIdentities <= 0
            || copy.MaxIdentityCharacters <= 0 || copy.MaxEphemeralIdentities <= 0 || copy.MaxTenantEphemeralIdentities <= 0)
            throw new ArgumentOutOfRangeException(nameof(NativeClientCacheOptions));
        return copy;
    }
}
