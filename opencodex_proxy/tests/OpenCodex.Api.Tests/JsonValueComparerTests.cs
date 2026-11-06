using System.Text.Json;
using OpenCodex.CoreBase.Abstractions;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class JsonValueComparerTests
{
    [Fact]
    public void ObjectsCompareWithoutDependingOnInsertionOrder()
    {
        var left = new D
        {
            ["type"] = "message",
            ["content"] = new D { ["text"] = "hello", ["count"] = 1 }
        };
        var right = new D
        {
            ["content"] = new D { ["count"] = 1L, ["text"] = "hello" },
            ["type"] = "message"
        };

        Assert.True(JsonValueComparer.AreEqual(left, right));
    }

    [Fact]
    public void ArraysCompareByValueAndOrder()
    {
        var left = new List<object?>
        {
            new D { ["type"] = "input_text", ["text"] = "a" },
            new D { ["type"] = "input_text", ["text"] = "b" }
        };
        var reordered = new List<object?>
        {
            new D { ["type"] = "input_text", ["text"] = "b" },
            new D { ["type"] = "input_text", ["text"] = "a" }
        };

        Assert.True(JsonValueComparer.AreEqual(left, left));
        Assert.False(JsonValueComparer.AreEqual(left, reordered));
    }

    [Fact]
    public void JsonElementComparesWithParsedObjectGraph()
    {
        using var document = JsonDocument.Parse(
            """{"type":"message","content":[{"type":"input_text","text":"hello"}]}""");
        var parsed = new D
        {
            ["content"] = new List<object?>
            {
                new D { ["text"] = "hello", ["type"] = "input_text" }
            },
            ["type"] = "message"
        };

        Assert.True(JsonValueComparer.AreEqual(document.RootElement, parsed));
        Assert.True(JsonValueComparer.AreEqual(parsed, document.RootElement));
    }

    [Fact]
    public void JsonElementObjectsCompareWithoutDependingOnPropertyOrder()
    {
        using var left = JsonDocument.Parse("""{"type":"message","value":1}""");
        using var right = JsonDocument.Parse("""{"value":1,"type":"message"}""");

        Assert.True(JsonValueComparer.AreEqual(left.RootElement, right.RootElement));
    }
}
