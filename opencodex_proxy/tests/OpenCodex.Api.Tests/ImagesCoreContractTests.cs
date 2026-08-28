using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ImagesCoreContractTests
{
    [Fact]
    public void Parameters_PreserveUnknownFieldsWhenReplacingModel()
    {
        var parameters = new ImageProxyParameters(new Dictionary<string, object?>
        {
            ["model"] = "public-model",
            ["prompt"] = "draw",
            ["aspect_ratio"] = "16:9",
            ["resolution"] = "2k",
            ["storage_options"] = new Dictionary<string, object?> { ["bucket"] = "images" }
        });

        var mapped = parameters.WithModel("upstream-model");

        Assert.Equal("public-model", parameters.Model);
        Assert.Equal("upstream-model", mapped.Model);
        Assert.Equal("draw", mapped.Prompt);
        Assert.Equal("16:9", mapped.Payload["aspect_ratio"]);
        Assert.Equal("2k", mapped.Payload["resolution"]);
        Assert.Same(parameters.Payload["storage_options"], mapped.Payload["storage_options"]);
    }

    [Fact]
    public async Task FileSource_ReopensFreshStreamsAndCarriesMultipartMetadata()
    {
        var source = File("image[]", "image.png", 3);

        await using var first = await source.OpenReadAsync();
        await using var second = await source.OpenReadAsync();

        Assert.NotSame(first, second);
        Assert.Equal("image[]", source.FieldName);
        Assert.Equal("image.png", source.FileName);
        Assert.Equal("image/png", source.ContentType);
        Assert.Equal(3, source.Length);
        Assert.Equal(1, first.ReadByte());
        Assert.Equal(1, second.ReadByte());
    }

    [Fact]
    public void EditRequest_PreservesMultipleImageOrder()
    {
        var first = File("image[]", "first.png", 3);
        var second = File("image[]", "second.png", 3);
        var request = new ImageEditRequest(
            new ImageProxyParameters(new Dictionary<string, object?> { ["prompt"] = "edit" }),
            [first, second]);

        Assert.Equal(["first.png", "second.png"], request.Images.Select(image => image.FileName));
    }

    [Fact]
    public async Task UpstreamResponse_DisposesContentAndOwnerOnlyOnce()
    {
        var content = new TrackingStream();
        var owner = new TrackingDisposable();
        var response = new ImageUpstreamResponse(200, "application/json", content, owner: owner);

        await response.DisposeAsync();
        await response.DisposeAsync();

        Assert.Equal(1, content.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task RouteInterfaceDefaultOverload_FiltersAllowedChannelTypes()
    {
        IProxyRouteService routes = new LegacyRouteService(
        [
            Route("chat"),
            Route("images")
        ]);

        var filtered = await routes.ListRouteCandidatesAsync(
            "admin",
            "model",
            new HashSet<string>(StringComparer.Ordinal) { "images" });

        Assert.Single(filtered);
        Assert.Equal("images", filtered[0].Channel["type"]);
    }

    private static ImageProxyFileSource File(string fieldName, string fileName, long length)
        => new(fieldName, fileName, "image/png", length,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    private static ProxyRouteDto Route(string type)
        => new(new Dictionary<string, object?> { ["type"] = type }, "model", "model", false, false);

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingStream : MemoryStream
    {
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class LegacyRouteService(IReadOnlyList<ProxyRouteDto> routes) : IProxyRouteService
    {
        public Task<IReadOnlyList<ProxyRouteDto>> ListRouteCandidatesAsync(string ownerUsername, string? model)
            => Task.FromResult(routes);

        public Task<VisionTransferRoutesDto> ListVisionTransferRoutesAsync(string ownerUsername)
            => Task.FromResult(VisionTransferRoutesDto.NotConfigured());

        public Task<IReadOnlyList<ProxyModelCapabilityDto>> ListModelCapabilitiesAsync(string ownerUsername)
            => Task.FromResult<IReadOnlyList<ProxyModelCapabilityDto>>([]);
    }
}
