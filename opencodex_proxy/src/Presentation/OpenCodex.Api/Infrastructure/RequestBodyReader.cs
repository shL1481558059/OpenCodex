using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Infrastructure;

public sealed class RequestBodyReader : IRequestBodyReader
{
    private static readonly object RawBodyItemKey = new();

    /// <summary>
    /// 标记超限请求体未读到 EOF。连接上仍有未消费字节时 Kestrel 会在响应结束后中止连接，
    /// 复用同一 keep-alive 连接的下一个请求会直接失败，因此写出 413 的一方必须显式关闭该连接。
    /// </summary>
    internal static readonly object OversizedBodyNotDrainedItemKey = new();

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

    /// <summary>
    /// 有界消费超限请求体的剩余字节，使 Kestrel 能在写出 413 后干净收尾。
    /// 声明长度超限时本读取器按契约不读取请求体，因此限制由服务端边界调用此方法完成。
    /// </summary>
    /// <param name="request">当前请求。</param>
    /// <param name="maxBytes">生效的请求预算（超限判定所用的同一来源）。</param>
    /// <param name="alreadyRead">调用方已读取的字节数。</param>
    /// <param name="cancellationToken">请求取消令牌。</param>
    /// <returns>读到 EOF 返回 <see langword="true"/>；达到 drain 上界或读取失效返回 <see langword="false"/>。</returns>
    internal static async Task<bool> DrainOversizedBodyAsync(
        HttpRequest request,
        long maxBytes,
        long alreadyRead,
        CancellationToken cancellationToken)
    {
        // 上界同样由已配置预算推导（最多再读一个预算），不引入新的硬编码容量；
        // 恶意客户端也无法靠超大超限体长期占住连接。
        var bound = checked(maxBytes * 2);
        var readBuffer = new byte[ProtocolConversionParameters.Current.FlushBytes];
        var total = alreadyRead;
        try
        {
            while (total <= bound)
            {
                var read = await request.Body.ReadAsync(readBuffer, cancellationToken);
                if (read == 0) return true;
                total += read;
            }

            return false;
        }
        catch (BadHttpRequestException)
        {
            // Kestrel 自身的 wire 上限先中止了读取，剩余字节已无法消费。
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        // 按 Content-Length 预分配，避免 MemoryStream 扩容时的多次拷贝；
        // 只保留这一份 UTF-8 字节：既用于解析，也作为日志正文的原文来源。
        var maxBytes = ProtocolConversionParameters.Current.MaxRequestBodyBytes;
        if (request.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            // 声明长度已能判定超限：不读取请求体。服务端边界（ProxyRequestBodyLimitAttribute）
            // 负责在写出 413 之前把这段 body 消费掉。
            throw BodyTooLarge();
        }

        var capacity = request.ContentLength is > 0 and <= int.MaxValue
            ? (int)request.ContentLength.Value
            : 0;
        using var buffer = new MemoryStream(capacity);
        var readBuffer = new byte[ProtocolConversionParameters.Current.FlushBytes];
        long total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(readBuffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
            {
                // 流式发现超限：交给边界（ProxyRequestBodyLimitAttribute）在写 413 前
                // 统一做有界 drain，保证 keep-alive 连接不被未消费的请求体污染。
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
