using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.Core.Errors;

namespace OpenCodex.Api.Infrastructure;

public interface IImageEditRequestReader
{
    Task<ImageEditRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

public sealed class ImageEditRequestReader : IImageEditRequestReader
{
    internal const long MaxFileBytes = 20 * 1024 * 1024;
    internal const long MaxTotalFileBytes = 100 * 1024 * 1024;
    internal const int MaxImageCount = 16;
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp"
    };

    public async Task<ImageEditRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.HasFormContentType)
        {
            throw Error("图片编辑只接受 multipart/form-data", StatusCodes.Status415UnsupportedMediaType);
        }
        if (request.ContentLength > MaxTotalFileBytes)
        {
            throw Error("上传文件总大小超限", StatusCodes.Status413PayloadTooLarge);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var parameters = form.ToDictionary(
            pair => pair.Key,
            pair => (object?)(pair.Value.Count == 1 ? pair.Value[0] : pair.Value.ToArray()),
            StringComparer.Ordinal);
        if (IsTrue(parameters.GetValueOrDefault("stream")))
        {
            throw Error("图片接口首版不支持 stream=true", StatusCodes.Status400BadRequest);
        }

        if (form.Files.Sum(file => file.Length) > MaxTotalFileBytes)
            throw Error("上传文件总大小超限", StatusCodes.Status413PayloadTooLarge);

        var images = new List<ImageProxyFileSource>();
        ImageProxyFileSource? mask = null;
        foreach (var file in form.Files)
        {
            if (file.Name is "image" or "image[]")
            {
                if (images.Count >= MaxImageCount)
                {
                    throw Error("图片文件数量超限", StatusCodes.Status413PayloadTooLarge);
                }
                images.Add(await CreateSourceAsync(file, cancellationToken));
                continue;
            }

            if (file.Name == "mask")
            {
                if (mask is not null)
                {
                    throw Error("mask 只能上传一个文件", StatusCodes.Status400BadRequest);
                }
                mask = await CreateSourceAsync(file, cancellationToken);
                continue;
            }

            throw Error($"不支持的文件字段：{file.Name}", StatusCodes.Status400BadRequest);
        }

        if (images.Count == 0)
        {
            throw Error("缺少 image 文件", StatusCodes.Status400BadRequest);
        }
        if (!parameters.TryGetValue("model", out var model) || string.IsNullOrWhiteSpace(model as string))
        {
            throw Error("缺少 model", StatusCodes.Status400BadRequest);
        }
        if (!parameters.TryGetValue("prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt as string))
        {
            throw Error("缺少 prompt", StatusCodes.Status400BadRequest);
        }

        return new ImageEditRequest(new ImageProxyParameters(parameters), images, mask);
    }

    private static async Task<ImageProxyFileSource> CreateSourceAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            throw Error($"文件 {file.FileName} 不能为空", StatusCodes.Status400BadRequest);
        }
        if (file.Length > MaxFileBytes)
        {
            throw Error($"文件 {file.FileName} 超过大小限制", StatusCodes.Status413PayloadTooLarge);
        }
        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            throw Error($"文件 {file.FileName} 的 MIME 类型不受支持", StatusCodes.Status415UnsupportedMediaType);
        }

        await using var input = file.OpenReadStream();
        var signature = new byte[12];
        var read = await input.ReadAsync(signature.AsMemory(), cancellationToken);
        if (!MatchesSignature(file.ContentType, signature.AsSpan(0, read)))
            throw Error($"文件 {file.FileName} 的内容与 MIME 类型不匹配", StatusCodes.Status415UnsupportedMediaType);
        return new ImageProxyFileSource(
            file.Name,
            file.FileName,
            file.ContentType,
            file.Length,
            _ => ValueTask.FromResult(file.OpenReadStream()));
    }

    private static bool IsTrue(object? value)
        => value is string text && bool.TryParse(text, out var parsed) && parsed;

    private static bool MatchesSignature(string contentType, ReadOnlySpan<byte> bytes)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff }),
            "image/webp" => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes[8..12].SequenceEqual("WEBP"u8),
            _ => false
        };
    }

    private static BadRequestException Error(string message, int statusCode)
        => new(message, statusCode);
}
