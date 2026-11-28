using System.Net.WebSockets;
using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Protocols;
using OpenCodex.CoreBase.Abstractions;

namespace OpenCodex.Api.Infrastructure;

/// <summary>Reads one complete request, releasing its UTF-8 buffer before the long-lived handler continues.</summary>
internal static class WebSocketRequestReader
{
    internal static async Task<Dictionary<string, object?>?> ReadAsync(WebSocket socket, long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[ProtocolConversionParameters.Current.FlushBytes];
        WebSocketReceiveResult fragment;
        do
        {
            fragment = await socket.ReceiveAsync(buffer, cancellationToken);
            if (fragment.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", cancellationToken);
                return null;
            }
            if (fragment.MessageType != WebSocketMessageType.Text)
                throw new JsonException("Only text JSON messages are accepted.");
            if (message.Length + fragment.Count > maximumBytes)
                throw new RequestBudgetExceededException(maximumBytes, message.Length + fragment.Count);
            message.Write(buffer, 0, fragment.Count);
        } while (!fragment.EndOfMessage);
        if (!message.TryGetBuffer(out var written)) throw new InvalidOperationException("The request buffer has no owned storage.");
        await using var payload = JsonPayload.FromOwnedUtf8(written.AsMemory(0, checked((int)message.Length)));
        return await payload.MaterializeObjectAndReleaseAsync(cancellationToken)
            ?? throw new JsonException("A JSON object is required.");
    }
}
