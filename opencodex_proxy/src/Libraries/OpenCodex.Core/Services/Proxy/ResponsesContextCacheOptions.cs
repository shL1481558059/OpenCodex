namespace OpenCodex.Core.Services.Proxy;

public sealed class ResponsesContextCacheOptions
{
    public long MaxBytes { get; set; }=128L*1024*1024;
    public long MaxTenantBytes { get; set; }=32L*1024*1024;
    public long MaxSnapshotBytes { get; set; }=16L*1024*1024;
    public int MaxSnapshots { get; set; }=2000;
    public int SnapshotTtlSeconds { get; set; }=900;
    public int CleanupIntervalSeconds { get; set; }=60;
    public int MaxConversationLocks { get; set; }=2048;
    public int MaxWaitersPerConversation { get; set; }=128;
    public int MaxProbes { get; set; }=1024;
    public int ProbeTtlSeconds { get; set; }=300;
    internal ResponsesContextCacheOptions CopyValidated()
    {
        var copy=(ResponsesContextCacheOptions)MemberwiseClone();
        if(copy.MaxBytes>long.MaxValue/8 || copy.MaxTenantBytes>long.MaxValue/8 || copy.MaxSnapshotBytes>long.MaxValue/8 || copy.MaxBytes<=0 || copy.MaxTenantBytes<=0 || copy.MaxSnapshotBytes<=0 || copy.MaxSnapshots<=0 || copy.SnapshotTtlSeconds<=0
            || copy.CleanupIntervalSeconds<=0 || copy.MaxConversationLocks<=0 || copy.MaxWaitersPerConversation<=0 || copy.MaxWaitersPerConversation==int.MaxValue
            || copy.MaxProbes<=0 || copy.ProbeTtlSeconds<=0)throw new ArgumentOutOfRangeException(nameof(ResponsesContextCacheOptions));
        return copy;
    }
}
