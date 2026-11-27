using System.Text.Json;

namespace OpenCodex.ProtocolConversion;

internal sealed class ProtocolConversionParameters
{
    internal static ProtocolConversionParameters Current { get; } = Load();

    public required int StringSegmentCharacters { get; init; }
    public required int FlushBytes { get; init; }
    public required int GeneratedFrameCacheBytes { get; init; }
    public required int CaptureCopyCharacters { get; init; }
    public required int SseReadBufferCharacters { get; init; }
    public required int SseRetainedBufferCharacters { get; init; }
    public required int SseMaxEventBytes { get; init; }
    public required int SseMaxCaptureBytes { get; init; }
    public required int SseMaxCaptureItems { get; init; }
    public required int SseMaxCaptureFields { get; init; }
    public required long SseMaxBufferedRoundBytes { get; init; }
    public required int SseMaxBufferedRoundFrames { get; init; }
    public required int SseMaxProbeBytes { get; init; }
    public required int SseMaxProbeFrames { get; init; }
    public required int SseMaxWebSocketMessageBytes { get; init; }
    public required int SseMaxBatchBytes { get; init; }
    public required int SseMaxFlushDelayMilliseconds { get; init; }
    public required int SseMaxQueuedEvents { get; init; }
    public required int SseMaxQueuedBytes { get; init; }

    private static ProtocolConversionParameters Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-conversion.parameters.json");
        using var source = File.OpenRead(path);
        var parameters = JsonSerializer.Deserialize<ProtocolConversionParameters>(source)
            ?? throw new JsonException("Protocol conversion parameters must be a JSON object.");
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.StringSegmentCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.FlushBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.GeneratedFrameCacheBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.CaptureCopyCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseReadBufferCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseRetainedBufferCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxEventBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxCaptureFields);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBufferedRoundBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBufferedRoundFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxProbeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxProbeFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxWebSocketMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxFlushDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxQueuedEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameters.SseMaxQueuedBytes);
        return parameters;
    }
}
