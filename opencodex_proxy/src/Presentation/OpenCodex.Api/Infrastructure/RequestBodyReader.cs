using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Infrastructure;

public sealed class RequestBodyReader : IRequestBodyReader
{
    private static readonly object RawBodyItemKey = new();

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
        var maxBytes = request.HttpContext.RequestServices?.GetService<IConfiguration>() is { } configuration
            ? ProxyRequestLimits.ResolveMaxRequestBodyBytes(configuration)
            : ProxyRequestLimits.DefaultMaxRequestBodyMegabytes * 1024L * 1024L;
        if (request.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            throw BodyTooLarge();
        }

        var capacity = request.ContentLength is > 0 and <= int.MaxValue
            ? (int)request.ContentLength.Value
            : 0;
        var buffer = new MemoryStream(capacity);
        var readBuffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(readBuffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                throw BodyTooLarge();
            }
            buffer.Write(readBuffer, 0, read);
        }

        var written = (int)buffer.Length;
        if (written == 0) return ReadOnlyMemory<byte>.Empty;
        var bytes = new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, written);
        var captured = ProxyLogBodyHandle.AdoptOwnedUtf8(bytes);
        request.HttpContext.Items[RawBodyItemKey] = captured;
        request.HttpContext.Response.OnCompleted(() =>
        {
            captured.Dispose();
            return Task.CompletedTask;
        });
        return bytes;
    }

    private static BadHttpRequestException BodyTooLarge() =>
        new("request body too large", StatusCodes.Status413PayloadTooLarge);
}
