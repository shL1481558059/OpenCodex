namespace OpenCodex.Api.Tests;

/// <summary>
/// Explicit scales for the long-history / WebSocket regression fixtures. These are test-only
/// sizes and timeouts, not production capacity; all production capacity comes from the
/// mandatory root parameter file. Kept in source so the tests have no uncommitted dependency.
/// </summary>
internal static class WsHistoryTestParameters
{
    internal const int HistoricalRequestBytes = 37_681_195;
    internal const int SmallRequestBytes = 16_384;
    internal const int ReceiveFragmentBytes = 16_384;
    internal const int CaseTimeoutMilliseconds = 30_000;
    internal const int OutputEventMaxBytes = 16_384;
    internal const int OutputCaptureMaxBytes = 8_192;
    internal const int OutputDeltaCharacters = 4_096;
    internal const int OutputItemCount = 3;
    internal const int CacheHistoryVersions = 300;
    internal const int CacheConcurrentWorkers = 16;
    internal const int CacheWsWarmupVersions = 3;
    internal const int SnapshotOrphanAgeSeconds = 1;
}
