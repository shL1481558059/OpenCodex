using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Config;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Core.ExternalIntegrations;

public sealed partial class HttpUpstreamClient
{
    private const int MaxImagesErrorBytes = 64 * 1024;
    private static readonly HashSet<string> SafeImagesResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-request-id", "request-id", "openai-request-id", "retry-after"
    };
    private static readonly HashSet<string> UnsupportedXaiEditParameters = new(StringComparer.Ordinal)
    {
        "size", "quality", "background", "output_format", "output_compression", "moderation", "style"
    };

    public Task<ImageUpstreamResponse> GenerateAsync(
        IReadOnlyDictionary<string, object?> channel,
        ImageGenerationRequest request,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        if (ImagesDialect(channel) == "xai") ValidateXaiParameters(request.Parameters.Payload, "generations");
        return SendImagesAsync(channel, BuildImagesJsonRequest(channel, request.Parameters.Payload, "/images/generations"), defaultTimeout, cancellationToken);
    }

    public async Task<ImageUpstreamResponse> EditAsync(
        IReadOnlyDictionary<string, object?> channel,
        ImageEditRequest request,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        var dialect = ImagesDialect(channel);
        if (dialect == "xai")
        {
            ValidateXaiEdit(request);
            using var outbound = BuildXaiEditRequest(channel, request);
            return await SendImagesAsync(channel, outbound, defaultTimeout, cancellationToken);
        }

        if (dialect != "openai")
        {
            throw new BadRequestException($"unsupported images API dialect: {dialect}");
        }

        using var multipart = await BuildOpenAiEditRequestAsync(channel, request, cancellationToken);
        return await SendImagesAsync(channel, multipart, defaultTimeout, cancellationToken);
    }

    private async Task<ImageUpstreamResponse> SendImagesAsync(
        IReadOnlyDictionary<string, object?> channel,
        HttpRequestMessage request,
        int defaultTimeout,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutValue(JsonDictionaryValue.Get(channel, "timeout_seconds"), defaultTimeout)));
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpstreamException("upstream request timed out", ProxyHttpStatus.GatewayTimeout,
                    channelId: JsonDictionaryValue.String(channel, "id"));
            }
            catch (HttpRequestException exception)
            {
                throw new UpstreamException($"failed to reach upstream: {exception.Message}", ProxyHttpStatus.BadGateway,
                    channelId: JsonDictionaryValue.String(channel, "id"));
            }

            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var body = await ReadBoundedErrorAsync(response.Content, cancellationToken);
                    throw new UpstreamException($"upstream returned HTTP {(int)response.StatusCode}", (int)response.StatusCode,
                        body, JsonDictionaryValue.String(channel, "id"));
                }
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new ImageUpstreamResponse(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                stream,
                SafeHeaders(response),
                new ImagesHttpResponseOwner(response));
        }
    }

    private static HttpRequestMessage BuildImagesJsonRequest(
        IReadOnlyDictionary<string, object?> channel,
        IReadOnlyDictionary<string, object?> payload,
        string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, JoinUrl(JsonDictionaryValue.String(channel, "baseurl"), endpoint))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyImagesHeaders(request, channel, includeContentType: false);
        return request;
    }

    private static async Task<HttpRequestMessage> BuildOpenAiEditRequestAsync(
        IReadOnlyDictionary<string, object?> channel,
        ImageEditRequest request,
        CancellationToken cancellationToken)
    {
        var multipart = new MultipartFormDataContent();
        try
        {
            foreach (var (key, value) in request.Parameters.Payload)
            {
                multipart.Add(new StringContent(ScalarText(value), Encoding.UTF8), key);
            }
            foreach (var file in request.Images.Concat(request.Mask is null ? [] : [request.Mask]))
            {
                var stream = await file.OpenReadAsync(cancellationToken);
                var content = new StreamContent(stream);
                if (!string.IsNullOrWhiteSpace(file.ContentType)) content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                if (file.Length is { } length) content.Headers.ContentLength = length;
                multipart.Add(content, file.FieldName, file.FileName);
            }
            var outbound = new HttpRequestMessage(HttpMethod.Post, JoinUrl(JsonDictionaryValue.String(channel, "baseurl"), "/images/edits")) { Content = multipart };
            ApplyImagesHeaders(outbound, channel, includeContentType: false);
            return outbound;
        }
        catch
        {
            multipart.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage BuildXaiEditRequest(
        IReadOnlyDictionary<string, object?> channel,
        ImageEditRequest request)
    {
        var outbound = new HttpRequestMessage(HttpMethod.Post, JoinUrl(JsonDictionaryValue.String(channel, "baseurl"), "/images/edits"))
        {
            Content = new XaiEditContent(request.Parameters.Payload, request.Images)
        };
        ApplyImagesHeaders(outbound, channel, includeContentType: false);
        return outbound;
    }

    private static void ValidateXaiEdit(ImageEditRequest request)
    {
        if (request.Mask is not null) throw new BadRequestException("xAI images edits do not support mask");
        if (request.Images.Count is < 1 or > 3) throw new BadRequestException("xAI images edits require between 1 and 3 images");
        var unsupported = request.Parameters.Payload.Keys.FirstOrDefault(UnsupportedXaiEditParameters.Contains);
        if (unsupported is not null) throw new BadRequestException($"xAI images edits do not support parameter: {unsupported}");
    }

    private static void ValidateXaiParameters(IReadOnlyDictionary<string, object?> payload, string operation)
    {
        var unsupported = payload.Keys.FirstOrDefault(UnsupportedXaiEditParameters.Contains);
        if (unsupported is not null) throw new BadRequestException($"xAI images {operation} do not support parameter: {unsupported}");
    }

    private static string ImagesDialect(IReadOnlyDictionary<string, object?> channel)
        => ConfigValue.TryAsObject(JsonDictionaryValue.Get(channel, "compat"), out var compat)
            ? JsonDictionaryValue.String(compat, "images_api_dialect") : string.Empty;

    private static void ApplyImagesHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, object?> channel, bool includeContentType)
    {
        foreach (var header in BuildHeaders(channel))
        {
            if (!includeContentType && string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static string ScalarText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };

    private static IReadOnlyDictionary<string, string> SafeHeaders(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Where(header => SafeImagesResponseHeaders.Contains(header.Key))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

    private static async Task<string> ReadBoundedErrorAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxImagesErrorBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private sealed class ImagesHttpResponseOwner(HttpResponseMessage response) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            response.Dispose();
        }
    }

    private sealed class XaiEditContent : HttpContent
    {
        private readonly IReadOnlyDictionary<string, object?> _payload;
        private readonly IReadOnlyList<ImageProxyFileSource> _images;

        public XaiEditContent(IReadOnlyDictionary<string, object?> payload, IReadOnlyList<ImageProxyFileSource> images)
        {
            _payload = payload;
            _images = images;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => SerializeAsync(stream, cancellationToken);

        private async Task SerializeAsync(Stream target, CancellationToken cancellationToken)
        {
            await target.WriteAsync("{"u8.ToArray(), cancellationToken);
            var first = true;
            foreach (var (key, value) in _payload)
            {
                if (!first) await target.WriteAsync(","u8.ToArray(), cancellationToken);
                first = false;
                await target.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(key), cancellationToken);
                await target.WriteAsync(":"u8.ToArray(), cancellationToken);
                await target.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken);
            }
            if (!first) await target.WriteAsync(","u8.ToArray(), cancellationToken);
            var plural = _images.Count > 1;
            await target.WriteAsync(Encoding.UTF8.GetBytes(plural ? "\"images\":[" : "\"image\":"), cancellationToken);
            for (var index = 0; index < _images.Count; index++)
            {
                if (index > 0) await target.WriteAsync(","u8.ToArray(), cancellationToken);
                var file = _images[index];
                await target.WriteAsync(Encoding.UTF8.GetBytes($"{{\"url\":\"data:{file.ContentType ?? "application/octet-stream"};base64,"), cancellationToken);
                await using var input = await file.OpenReadAsync(cancellationToken);
                using (var transform = new ToBase64Transform())
                using (var crypto = new CryptoStream(target, transform, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await input.CopyToAsync(crypto, cancellationToken);
                    crypto.FlushFinalBlock();
                }
                await target.WriteAsync("\"}"u8.ToArray(), cancellationToken);
            }
            if (plural) await target.WriteAsync("]"u8.ToArray(), cancellationToken);
            await target.WriteAsync("}"u8.ToArray(), cancellationToken);
        }
    }
}
