using System.Text.Json;
using OpenCodex.Core.ExternalIntegrations;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class JsonStreamBodyContentTests
{
    [Fact]
    public async Task LargeStringStreamsAsTheSameJsonGraphWithKnownLength()
    {
        var payload = new D
        {
            ["model"] = "test",
            ["input"] = new List<object?>
            {
                new D
                {
                    ["type"] = "message",
                    ["content"] = string.Concat(Enumerable.Repeat("中文\n\"x\"", 100_000))
                }
            }
        };
        using var content = new JsonStreamBodyContent(payload, "application/json");

        var bytes = await content.ReadAsByteArrayAsync();

        Assert.Equal(bytes.Length, content.Headers.ContentLength);
        using var expected = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var actual = JsonDocument.Parse(bytes);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }
}
