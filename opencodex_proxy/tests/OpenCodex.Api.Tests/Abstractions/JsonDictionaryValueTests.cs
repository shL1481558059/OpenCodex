using OpenCodex.Api.Abstractions;

namespace OpenCodex.Api.Tests.Abstractions;

public sealed class JsonDictionaryValueTests
{
    [Fact]
    public void GetAndStringReturnMissingAsNullOrEmptyAndTrimValues()
    {
        var dictionary = new Dictionary<string, object?>
        {
            ["text"] = " value ",
            ["number"] = 42
        };

        Assert.Equal(" value ", JsonDictionaryValue.Get(dictionary, "text"));
        Assert.Null(JsonDictionaryValue.Get(dictionary, "missing"));
        Assert.Equal("value", JsonDictionaryValue.String(dictionary, "text"));
        Assert.Equal("42", JsonDictionaryValue.String(dictionary, "number"));
        Assert.Equal(string.Empty, JsonDictionaryValue.String(dictionary, "missing"));
    }

    [Fact]
    public void ListReturnsListsEnumerableValuesAndEmptyForScalars()
    {
        var list = new List<object?> { "a" };
        var dictionary = new Dictionary<string, object?>
        {
            ["list"] = list,
            ["array"] = new object?[] { "b", "c" },
            ["text"] = "not-list"
        };

        Assert.Same(list, JsonDictionaryValue.List(dictionary, "list"));
        Assert.Equal(["b", "c"], JsonDictionaryValue.List(dictionary, "array"));
        Assert.Empty(JsonDictionaryValue.List(dictionary, "text"));
        Assert.Empty(JsonDictionaryValue.List(dictionary, "missing"));
    }

    [Fact]
    public void ObjectUsesCloneForDictionaryValues()
    {
        var nested = new Dictionary<string, object?> { ["id"] = "chat" };
        var dictionary = new Dictionary<string, object?>
        {
            ["channel"] = nested,
            ["text"] = "not-object"
        };

        var result = JsonDictionaryValue.Object(
            dictionary,
            "channel",
            value => value.ToDictionary(pair => pair.Key, pair => pair.Value));

        Assert.NotSame(nested, result);
        Assert.Equal("chat", result["id"]);
        Assert.Empty(JsonDictionaryValue.Object(dictionary, "text", _ => []));
    }
}
