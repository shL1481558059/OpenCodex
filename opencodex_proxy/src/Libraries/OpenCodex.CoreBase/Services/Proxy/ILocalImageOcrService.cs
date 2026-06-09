namespace OpenCodex.CoreBase.Services.Proxy;

public interface ILocalImageOcrService
{
    Task<string> RecognizeTextAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken);
}
