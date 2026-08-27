using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.DTOs.Models;
using OpenCodex.CoreBase.Services;

namespace OpenCodex.Core.Services;

/// <summary>
 /// 远端模型目录 JSON 拉取客户端。
 /// </summary>
public sealed class ModelCatalogSyncClient : IModelCatalogSyncClient
{
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB
    private const int BufferSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<ModelCatalogSyncClient> _logger;

    public ModelCatalogSyncClient(HttpClient httpClient, ILogger<ModelCatalogSyncClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ModelCatalogTransferDocument> FetchAsync(string url)
    {
        // Scheme validation: only http/https allowed (Q11).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidOperationException($"sync URL scheme is not http or https: {url}");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        // Pre-check Content-Length if available.
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxResponseBytes)
        {
            var body = await ReadAllAsync(response, MaxResponseBytes);
            var bodyText = System.Text.Encoding.UTF8.GetString(body);
            LogWarning(url, (int)response.StatusCode, "response exceeds 5 MB limit", bodyText);
            throw new InvalidOperationException($"sync response exceeds 5 MB limit ({contentLength} bytes)");
        }

        response.EnsureSuccessStatusCode();

        byte[] bodyBytes = await ReadAllAsync(response, MaxResponseBytes);
        // Strip UTF-8 BOM if present (System.Text.Json rejects it).
        bodyBytes = StripBom(bodyBytes);

        ModelCatalogTransferDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ModelCatalogTransferDocument>(bodyBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogWarning(url, (int)response.StatusCode, ex.Message, System.Text.Encoding.UTF8.GetString(bodyBytes));
            throw new InvalidOperationException($"sync JSON is invalid: {ex.Message}");
        }

        if (document is null)
        {
            LogWarning(url, (int)response.StatusCode, "document is null", System.Text.Encoding.UTF8.GetString(bodyBytes));
            throw new InvalidOperationException("sync document is null");
        }

        return document;
    }

    private async Task<byte[]> ReadAllAsync(HttpResponseMessage response, int maxBytes)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[BufferSize];
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                // Write what we have so far for the warning log.
                memory.Write(buffer, 0, maxBytes - (totalRead - read) > 0 ? maxBytes - (totalRead - read) : 0);
                var partial = System.Text.Encoding.UTF8.GetString(memory.ToArray());
                LogWarning(
                    response.RequestMessage?.RequestUri?.ToString() ?? "unknown",
                    (int)response.StatusCode,
                    "response exceeds 5 MB limit during streaming read",
                    partial);
                throw new InvalidOperationException("sync response exceeds 5 MB limit during streaming read");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsSpan(3).ToArray();
        }
        return bytes;
    }

    private void LogWarning(string url, int statusCode, string reason, string body)
    {
        // Q23: do not truncate the response body.
        _logger.LogWarning(
            "Model catalog sync failed. URL: {Url}, Status: {StatusCode}, Reason: {Reason}, Body: {Body}",
            url,
            statusCode,
            reason,
            body);
    }
}
