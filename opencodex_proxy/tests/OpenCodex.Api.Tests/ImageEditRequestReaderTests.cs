using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OpenCodex.Api.Infrastructure;
using OpenCodex.Core.Errors;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ImageEditRequestReaderTests
{
    [Fact]
    public async Task ReadAsync_PreservesImageOrderAndReturnsFreshStreams()
    {
        var first = PngFile("image", "first.png", 1);
        var second = PngFile("image[]", "second.png", 2);
        var request = FormRequest(
            new Dictionary<string, StringValues>
            {
                ["model"] = "gpt-image-1",
                ["prompt"] = "edit",
                ["unknown"] = "kept"
            },
            first,
            second);

        var result = await new ImageEditRequestReader().ReadAsync(request);

        Assert.Equal(["first.png", "second.png"], result.Images.Select(file => file.FileName));
        Assert.Equal("kept", result.Parameters.Payload["unknown"]);
        Stream stream1;
        await using (stream1 = await result.Images[0].OpenReadAsync())
        {
            Assert.Equal(0x89, stream1.ReadByte());
        }
        await using var stream2 = await result.Images[0].OpenReadAsync();
        Assert.NotSame(stream1, stream2);
        Assert.Equal(0x89, stream2.ReadByte());
    }

    [Theory]
    [InlineData("application/octet-stream", 415)]
    [InlineData("image/png", 415)]
    public async Task ReadAsync_RejectsInvalidMimeOrMagic(string contentType, int statusCode)
    {
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "image", "bad.bin")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
        var request = FormRequest(
            new Dictionary<string, StringValues> { ["model"] = "m", ["prompt"] = "p" },
            file);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => new ImageEditRequestReader().ReadAsync(request));

        Assert.Equal(statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedContentLengthBeforeReadingForm()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.ContentLength = 100L * 1024 * 1024 + 1;

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => new ImageEditRequestReader().ReadAsync(context.Request));

        Assert.Equal(413, exception.StatusCode);
    }

    private static HttpRequest FormRequest(Dictionary<string, StringValues> fields, params IFormFile[] files)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        var collection = new FormFileCollection();
        foreach (var file in files) collection.Add(file);
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(fields, collection)));
        return context.Request;
    }

    private static IFormFile PngFile(string field, string name, byte suffix)
    {
        byte[] content = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, suffix];
        return new FormFile(new MemoryStream(content), 0, content.Length, field, name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }
}
