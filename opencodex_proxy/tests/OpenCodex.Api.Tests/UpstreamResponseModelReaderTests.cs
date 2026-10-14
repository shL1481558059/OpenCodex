using OpenCodex.Core.Protocols;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class UpstreamResponseModelReaderTests
{
    [Fact]
    public void ReadsTopLevelModel()
    {
        var response = new Dictionary<string, object?> { ["model"] = "gpt-test" };

        Assert.Equal("gpt-test", UpstreamResponseModelReader.Read(response));
    }

    [Fact]
    public void PrefersClineEnvelopeDataModel()
    {
        var response = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["model"] = "deepseek-v4.1-flash",
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = "chat.completion",
                ["model"] = "deepseek/deepseek-v4.1-flash",
                ["choices"] = new List<object?>()
            }
        };

        Assert.Equal("deepseek/deepseek-v4.1-flash", UpstreamResponseModelReader.Read(response));
    }

    [Theory]
    [InlineData("chat.completion")]
    [InlineData("chat.completion.chunk")]
    public void ReadsDataModelWhenObjectMarksChatEnvelope(string objectType)
    {
        var response = new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = objectType,
                ["model"] = "wrapped-model"
            }
        };

        Assert.Equal("wrapped-model", UpstreamResponseModelReader.Read(response));
    }

    [Fact]
    public void ReadsDataModelWhenChoicesExist()
    {
        var response = new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?>
            {
                ["model"] = "wrapped-model",
                ["choices"] = new List<object?>()
            }
        };

        Assert.Equal("wrapped-model", UpstreamResponseModelReader.Read(response));
    }

    [Fact]
    public void IgnoresDataObjectThatIsNotAChatEnvelope()
    {
        var response = new Dictionary<string, object?>
        {
            ["model"] = "top-level",
            ["data"] = new Dictionary<string, object?> { ["model"] = "nested" }
        };

        Assert.Equal("top-level", UpstreamResponseModelReader.Read(response));
    }

    [Fact]
    public void FallsBackToResponseThenMessageModel()
    {
        var responseModel = new Dictionary<string, object?>
        {
            ["response"] = new Dictionary<string, object?> { ["model"] = "resp-model" }
        };
        var messageModel = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?> { ["model"] = "msg-model" }
        };

        Assert.Equal("resp-model", UpstreamResponseModelReader.Read(responseModel));
        Assert.Equal("msg-model", UpstreamResponseModelReader.Read(messageModel));
    }

    [Fact]
    public void TopLevelModelWinsOverNestedResponseModel()
    {
        var response = new Dictionary<string, object?>
        {
            ["model"] = "top-level",
            ["response"] = new Dictionary<string, object?> { ["model"] = "nested" }
        };

        Assert.Equal("top-level", UpstreamResponseModelReader.Read(response));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankModelIsMissing(string model)
    {
        var response = new Dictionary<string, object?> { ["model"] = model };

        Assert.Null(UpstreamResponseModelReader.Read(response));
    }

    [Fact]
    public void NonStringModelAndNullResponseAreMissing()
    {
        var response = new Dictionary<string, object?> { ["model"] = 12 };

        Assert.Null(UpstreamResponseModelReader.Read(response));
        Assert.Null(UpstreamResponseModelReader.Read(null));
    }
}
