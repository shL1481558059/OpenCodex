using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Json;
using OpenCodex.ProtocolConversion.Protocols;

namespace OpenCodex.Core.ExternalIntegrations;

/// <summary>Shared model/tool JSON ingress policy. Limits come from the root; no private store or ambient credits.</summary>
internal static class BoundedJsonResponseReader
{
    internal const string CaptureLimitCode = "response_capture_limit";
    internal const string InvalidJsonCode = "invalid_upstream_json";

    internal static long MaxJsonBytes(int captureMaxBytes, int overflowBlockBytes)
        => UpstreamJsonEvidence.MaxJsonBytes(captureMaxBytes, overflowBlockBytes);

    internal static void RejectContentLength(
        HttpResponseMessage response, int captureMaxBytes, int overflowBlockBytes, string? channelId = null)
    {
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > captureMaxBytes)
            throw Failure(response, ReadOnlyMemory<byte>.Empty, false, false, captureMaxBytes, overflowBlockBytes,
                CaptureLimitCode, "upstream JSON response exceeds configured capture byte limit", channelId);
    }

    internal static async Task<ReadOnlyMemory<byte>> ReadRawAsync(
        HttpResponseMessage response, int captureMaxBytes, int overflowBlockBytes,
        CancellationToken cancellationToken, string? channelId = null)
    {
        RejectContentLength(response, captureMaxBytes, overflowBlockBytes, channelId);
        // One owned C+F buffer. Preserve the complete last received block and stop immediately.
        var buffer = new byte[UpstreamJsonEvidence.MaxRawBytes(captureMaxBytes, overflowBlockBytes)];
        var receivedBytes = 0;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(receivedBytes, Math.Min(overflowBlockBytes, buffer.Length - receivedBytes)),
                    cancellationToken);
                if (read == 0) return buffer.AsMemory(0, receivedBytes);
                receivedBytes = checked(receivedBytes + read);
                if (receivedBytes > captureMaxBytes)
                    throw Failure(response, buffer.AsMemory(0, receivedBytes), true, false,
                        captureMaxBytes, overflowBlockBytes, CaptureLimitCode,
                        "upstream JSON response exceeds configured capture byte limit", channelId);
            }
        }
        catch (IOException exception)
            when (exception is not SnapshotPersistenceException)
        {
            throw new HttpRequestException("failed to read upstream response body", exception);
        }
    }

    internal static object? Parse(
        HttpResponseMessage response, ReadOnlyMemory<byte> ownedUtf8, int captureMaxBytes, int overflowBlockBytes,
        CancellationToken cancellationToken, string? channelId = null, Action<JsonElement>? inspectRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var json = ownedUtf8;
            if (json.Span.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
            using var document = JsonDocument.Parse(json);
            var parsed = WebSearchPayload.FromJsonElement(document.RootElement);
            EnsureNormalized(parsed, response, ownedUtf8, captureMaxBytes, overflowBlockBytes, cancellationToken, channelId);
            inspectRoot?.Invoke(document.RootElement);
            return parsed;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw Failure(response, ownedUtf8, true, true, captureMaxBytes, overflowBlockBytes,
                InvalidJsonCode, "upstream returned invalid JSON", channelId);
        }
    }

    internal static Dictionary<string, object?> RequireObject(
        object? parsed, HttpResponseMessage response, ReadOnlyMemory<byte> ownedUtf8,
        int captureMaxBytes, int overflowBlockBytes, string? channelId = null)
        => parsed as Dictionary<string, object?>
            ?? throw Failure(response, ownedUtf8, true, true, captureMaxBytes, overflowBlockBytes,
                InvalidJsonCode, "upstream returned a non-object JSON response", channelId);

    internal static void EnsureNormalized(
        object? value, HttpResponseMessage response, ReadOnlyMemory<byte> ownedUtf8,
        int captureMaxBytes, int overflowBlockBytes, CancellationToken cancellationToken, string? channelId = null)
    {
        try
        {
            if (ProtocolJsonWriter.Count(value, audit: true, cancellationToken) > captureMaxBytes)
                throw Failure(response, ownedUtf8, true, true, captureMaxBytes, overflowBlockBytes,
                    CaptureLimitCode, "normalized upstream JSON exceeds configured capture byte limit", channelId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw Failure(response, ownedUtf8, true, true, captureMaxBytes, overflowBlockBytes,
                InvalidJsonCode, "upstream JSON cannot be represented by the proxy", channelId);
        }
    }

    internal static UpstreamException Failure(
        HttpResponseMessage response, ReadOnlyMemory<byte> ownedPrefix, bool bodyRead, bool bodyComplete,
        int captureMaxBytes, int overflowBlockBytes, string errorCode, string message, string? channelId = null)
        => new(message, response.IsSuccessStatusCode || errorCode == CaptureLimitCode
                ? ProxyHttpStatus.BadGateway : (int)response.StatusCode,
            body: UpstreamJsonEvidence.Create(ownedPrefix, response.Content.Headers.ContentLength,
                (int)response.StatusCode, bodyRead, bodyComplete, captureMaxBytes, overflowBlockBytes),
            channelId: channelId, errorCode: errorCode);

    // No wire bytes exist for a caller-supplied/fake or already-combined parsed value.
    // Never pretend an empty byte envelope proves that those providers were unread.
    internal static UpstreamException ParsedFailure(
        int captureMaxBytes, string errorCode, string message, string stage, string? channelId = null)
        => new(message, ProxyHttpStatus.BadGateway,
            body: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_opencodex_capture"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["completed"] = false, ["source_reconstructed"] = false,
                    ["representation"] = "parsed_value_without_wire", ["wire_evidence_available"] = false,
                    ["capture_limit_bytes"] = captureMaxBytes, ["stage"] = stage
                }
            }, channelId: channelId, errorCode: errorCode);

    internal static void EnsureParsedBound(
        object? value, int captureMaxBytes, CancellationToken cancellationToken, string stage, string? channelId = null)
    {
        try
        {
            if (ProtocolJsonWriter.Count(value, audit: true, cancellationToken) > captureMaxBytes)
                throw ParsedFailure(captureMaxBytes, CaptureLimitCode,
                    "parsed tool-provider JSON exceeds configured capture byte limit", stage, channelId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw ParsedFailure(captureMaxBytes, InvalidJsonCode,
                "parsed tool-provider JSON cannot be represented by the proxy", stage, channelId);
        }
    }

    internal static long ValidateRequest(object? payload, JavaScriptEncoder encoder, CancellationToken cancellationToken)
    {
        var bytes = ProtocolJsonWriter.Measure(payload, encoder, cancellationToken);
        var maximum = ProtocolConversionParameters.Current.MaxRequestBodyBytes;
        if (bytes > maximum) throw new RequestBudgetExceededException(maximum, bytes);
        return bytes;
    }

    internal static bool IsPolicyFailure(Exception exception)
        => exception is RequestBudgetExceededException or SnapshotPersistenceException
            || exception is ProxyException { StatusCode: 413 or 507 }
            || exception is UpstreamException
            { ErrorCode: CaptureLimitCode or "response_capture_limit_exceeded" or InvalidJsonCode };
}
