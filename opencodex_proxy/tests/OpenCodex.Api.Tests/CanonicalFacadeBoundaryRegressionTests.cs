using System.Text.Json;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Errors;
using OpenCodex.ProtocolConversion.Models;
using Xunit;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

public sealed class CanonicalFacadeBoundaryRegressionTests
{
    private readonly ProtocolConversionFacade _facade = new();

    [Theory]
    [InlineData("incomplete", "stop")]
    [InlineData("completed", "length")]
    public void GeneratedResponseRejectsConflictingExplicitStatus(string status, string finishReason)
    {
        var response = new CanonicalResponse { Id = "generated", Text = "actual", Status = status, FinishReason = finishReason };
        Assert.Throws<BadRequestException>(() => _facade.ConvertResponse(response, ProtocolKind.Responses));
    }

    [Fact]
    public void GeneratedResponseHonorsConsistentExplicitIncompleteStatus()
    {
        var response = new CanonicalResponse { Id = "generated", Text = "actual", Status = "incomplete", FinishReason = "length" };
        var wire = _facade.ConvertResponse(response, ProtocolKind.Responses);
        Assert.Equal("incomplete", wire["status"]);
        Assert.Equal("max_output_tokens", Assert.IsType<D>(wire["incomplete_details"])["reason"]);
    }

    [Theory]
    [InlineData(ProtocolKind.Messages)]
    [InlineData(ProtocolKind.Responses)]
    public void MissingRawTextContainerRejectsAggregateEditExplicitly(ProtocolKind source)
    {
        var raw = new D { ["id"] = "failed", ["error"] = new D { ["type"] = "server_error", ["message"] = "actual failure" } };
        var original = _facade.ToCanonicalResponse(raw, source);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(original))!;
        node["Text"] = "new text";
        var edited = JsonSerializer.Deserialize<CanonicalResponse>(node.ToJsonString())!;
        Assert.Throws<BadRequestException>(() => _facade.ConvertResponse(edited, source));
        Assert.False(raw.ContainsKey(source == ProtocolKind.Messages ? "content" : "output"));
    }
}
