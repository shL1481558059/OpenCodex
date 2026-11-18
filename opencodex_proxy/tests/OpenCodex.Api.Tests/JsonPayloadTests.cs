using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class JsonPayloadTests
{
    [Fact]
    public async Task OwnedUtf8MaterializesOnceWithScalarKindsPreserved()
    {
        var source = new D { ["max_tokens"] = 1, ["model"] = "test" };
        await using var payload = JsonPayload.FromOwnedUtf8(JsonSerializer.SerializeToUtf8Bytes(source));

        var first = await payload.MaterializeObjectAsync();
        var second = await payload.MaterializeObjectAsync();

        Assert.Same(first, second);
        Assert.Equal(1, first!["max_tokens"]);
        Assert.Equal("test", first["model"]);
    }

    [Fact]
    public async Task FileBackedPayloadMaterializesAndOwnsCleanup()
    {
        var path = Path.Combine(Path.GetTempPath(), "ocxp-json-payload-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """{"model":"test","input":"file"}""");

        await using (var payload = JsonPayload.FromFile(path, deleteOnDispose: true))
        {
            var value = await payload.MaterializeObjectAsync();
            Assert.Equal("file", value!["input"]);
            Assert.Equal(new FileInfo(path).Length, payload.Length);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MaterializeAndReleaseDropsTheInMemorySourceAfterCachingTheObject()
    {
        await using var payload = JsonPayload.FromOwnedUtf8(
            JsonSerializer.SerializeToUtf8Bytes(new D { ["model"] = "test", ["input"] = "large" }));

        var materialized = await payload.MaterializeObjectAndReleaseAsync();
        var cached = await payload.MaterializeObjectAsync();

        Assert.Same(materialized, cached);
        Assert.Equal("large", materialized!["input"]);
        Assert.Throws<InvalidOperationException>(() => payload.OpenRead());
    }
}
