using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;

namespace OpenCodex.Core.ExternalIntegrations;

/// <summary>Exact received JSON-prefix evidence; never a reconstructed successful response.</summary>
internal static class UpstreamJsonEvidence
{
    private const string EvidenceType = "upstream_json_body_evidence";

    internal static int MaxRawBytes(int captureMaxBytes, int overflowBlockBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureMaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overflowBlockBytes);
        var bytes = checked((long)captureMaxBytes + overflowBlockBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, (long)Array.MaxLength);
        return checked((int)bytes);
    }

    // Core must reserve this exact Body-envelope bound before the model call.
    // Any additional error wrapper belongs to its own explicit pre-reservation.
    internal static long MaxJsonBytes(int captureMaxBytes, int overflowBlockBytes)
    {
        _ = MaxRawBytes(captureMaxBytes, overflowBlockBytes);
        var evidenceBytes = ReceivedFrameEvidence.MaxJsonBytes(
            captureMaxBytes, overflowBlockBytes, ReceivedEvidenceKind.Json);
        // The same envelope writer is measured with maximum legal counter widths.
        // False is longer than true; null is the placeholder for the nested evidence.
        var wrapper = CreateEnvelope(null, long.MaxValue, int.MaxValue, false, false);
        var wrapperBytes = ProtocolJsonWriter.Count(wrapper, audit: true, CancellationToken.None)
            - ProtocolJsonWriter.Count(null, audit: true, CancellationToken.None);
        return checked(evidenceBytes + wrapperBytes);
    }

    internal static Dictionary<string, object?> Create(
        ReadOnlyMemory<byte> ownedPrefix, long? declaredContentLength, int upstreamStatusCode,
        bool bodyRead, bool bodyComplete, int captureMaxBytes, int overflowBlockBytes)
    {
        if (ownedPrefix.Length > MaxRawBytes(captureMaxBytes, overflowBlockBytes))
            throw new ArgumentException("Received JSON evidence exceeds its declared prefix bound.", nameof(ownedPrefix));
        if (!bodyRead && (!ownedPrefix.IsEmpty || bodyComplete))
            throw new ArgumentException("Unread HTTP-body evidence cannot claim received or complete body data.", nameof(bodyRead));
        var received = ReceivedFrameEvidence.CreateJsonEvidence(ownedPrefix, captureMaxBytes, overflowBlockBytes);
        return CreateEnvelope(received, declaredContentLength, upstreamStatusCode, bodyRead, bodyComplete);
    }

    internal static bool IsEvidence(object? value)
        => value is IReadOnlyDictionary<string, object?> envelope
            && envelope.TryGetValue("type", out var type)
            && type is string evidenceType && string.Equals(evidenceType, EvidenceType, StringComparison.Ordinal)
            && envelope.TryGetValue("received_json", out var received) && ReceivedFrameEvidence.IsEvidence(received);

    private static Dictionary<string, object?> CreateEnvelope(
        object? received, long? declaredContentLength, int upstreamStatusCode, bool bodyRead, bool bodyComplete)
        => new(StringComparer.Ordinal)
        {
            ["type"] = EvidenceType,
            ["body_read"] = bodyRead,
            ["body_complete"] = bodyComplete,
            ["declared_content_length"] = declaredContentLength,
            ["upstream_status_code"] = upstreamStatusCode,
            ["received_json"] = received
        };
}
