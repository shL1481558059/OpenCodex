namespace OpenCodex.Core.Services.MultiAgent;

public sealed class NativeClientStateCacheOptions
{
    public int RedisTtlHours { get; set; } = 24;

    public long RedisMaxValueBytes { get; set; } = 8L * 1024 * 1024;

    internal NativeClientStateCacheOptions ValidatedCopy()
    {
        var copy = (NativeClientStateCacheOptions)MemberwiseClone();
        if (copy.RedisTtlHours <= 0 || copy.RedisMaxValueBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(NativeClientStateCacheOptions));
        return copy;
    }
}
