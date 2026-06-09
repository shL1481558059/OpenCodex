using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using OpenCodex.Core.Errors;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Services.Proxy;

namespace OpenCodex.Core.Services.Proxy;

public sealed class LocalPaddleImageOcrService : ILocalImageOcrService, IDisposable
{
    private readonly IOpenCodexRuntimeSettingsProvider _settingsProvider;
    private readonly object _sync = new();
    private QueuedPaddleOcrAll? _ocr;
    private bool _disposed;

    public LocalPaddleImageOcrService(IOpenCodexRuntimeSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public async Task<string> RecognizeTextAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        if (imageBytes.Length == 0)
        {
            return string.Empty;
        }

        ThrowIfDisposed();

        try
        {
            using var image = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (image.Empty())
            {
                throw new InvalidOperationException("decoded image is empty");
            }

            var result = await GetOrCreateOcr().Run(
                image,
                recognizeBatchSize: 0,
                configure: null,
                cancellationToken: cancellationToken);
            return NormalizeLineEndings(result.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new UpstreamException(
                $"local OCR failed: {exception.Message}",
                ProxyHttpStatus.BadGateway,
                body: null,
                channelId: "__local__");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _ocr?.Dispose();
            _ocr = null;
            _disposed = true;
        }
    }

    private QueuedPaddleOcrAll GetOrCreateOcr()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_ocr is not null)
            {
                return _ocr;
            }

            var settings = _settingsProvider.GetSettings();
            _ocr = new QueuedPaddleOcrAll(
                () => new PaddleOcrAll(ResolveModel(settings.LocalOcrModel), PaddleDevice.Mkldnn())
                {
                    Enable180Classification = true,
                    AllowRotateDetection = true
                },
                consumerCount: 1,
                boundedCapacity: 32);
            return _ocr;
        }
    }

    private static FullOcrModel ResolveModel(string modelName)
    {
        return modelName.Trim().ToLowerInvariant() switch
        {
            "arabicv5" => LocalFullModels.ArabicV5,
            "chinesev5" => LocalFullModels.ChineseV5,
            "cyrillicv5" => LocalFullModels.CyrillicV5,
            "devanagariv5" => LocalFullModels.DevanagariV5,
            "eastslavicv5" => LocalFullModels.EastSlavicV5,
            "englishv5" => LocalFullModels.EnglishV5,
            "greekv5" => LocalFullModels.GreekV5,
            "koreanv5" => LocalFullModels.KoreanV5,
            "latinv5" => LocalFullModels.LatinV5,
            "tamilv5" => LocalFullModels.TamilV5,
            "teluguv5" => LocalFullModels.TeluguV5,
            "thaiv5" => LocalFullModels.ThaiV5,
            _ => throw new InvalidOperationException($"unsupported local OCR model: {modelName}")
        };
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
