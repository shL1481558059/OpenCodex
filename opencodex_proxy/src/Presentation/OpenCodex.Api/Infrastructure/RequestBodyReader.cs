using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Infrastructure;

public sealed class RequestBodyReader(ProxyAuditSpool? auditSpool = null) : IRequestBodyReader
{
    private static readonly object RawBodyItemKey = new();
    private readonly ProxyAuditSpool _auditSpool = auditSpool ?? ProxyAuditSpool.Default;

    public async Task<JsonPayload?> ReadJsonPayloadAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBodyAsync(request, cancellationToken);
        return bytes.Length == 0 ? null : JsonPayload.FromOwnedUtf8(bytes);
    }

    public async Task<Dictionary<string, object?>?> ReadJsonObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var payload = await ReadJsonPayloadAsync(request, cancellationToken);
            return payload is null ? null : await payload.MaterializeObjectAndReleaseAsync(cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ProxyLogBodyHandle? ReadCapturedRawBody(HttpRequest request)
    {
        return request.HttpContext.Items.TryGetValue(RawBodyItemKey, out var value)
            && value is ProxyLogBodyHandle captured
                ? captured
                : null;
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // 按 Content-Length 预分配，避免 MemoryStream 扩容时的多次拷贝；
        // 只保留这一份 UTF-8 字节：既用于解析，也作为日志正文的原文来源。
        var capacity = request.ContentLength is > 0 and <= int.MaxValue
            ? (int)request.ContentLength.Value
            : 0;
        var buffer = new MemoryStream(capacity);
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var written = (int)buffer.Length;
        var bytes = new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, written);
        var captured = _auditSpool.CaptureUtf8(bytes.Span, cancellationToken);
        request.HttpContext.Items[RawBodyItemKey] = captured;
        request.HttpContext.Response.OnCompleted(() =>
        {
            captured.Dispose();
            return Task.CompletedTask;
        });
        return bytes;
    }

}
