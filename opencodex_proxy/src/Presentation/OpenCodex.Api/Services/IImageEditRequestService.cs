using OpenCodex.CoreBase.Domain.Proxy;

namespace OpenCodex.Api.Services;

public interface IImageEditRequestService
{
    Task<ImageEditRequest> ReadAsync(HttpRequest request, CancellationToken cancellationToken = default);
}
