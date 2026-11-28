using System.Text;
using System.Text.Json;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class ModelStreamCaptureLimitRegressionTests
{
    [Fact]
    public async Task DoneItemsCumulativelyExceedCaptureBytesBeforeSuccessfulTerminal()
    {
        var parameters = OutputParameters.Load();
        var items = Enumerable.Range(0, parameters.OutputItemCount)
            .Select(_ => Message(new string('x', parameters.OutputDeltaCharacters)))
            .ToList();
        var events = items.Select((item, index) => Event(new Dictionary<string, object?>
        {
            ["type"] = "response.output_item.done",
            ["output_index"] = index,
            ["item"] = item
        })).Append(Event(Terminal([]))).ToArray();
        Assert.All(events, value => Assert.True(Encoding.UTF8.GetByteCount(value) <= parameters.OutputEventMaxBytes));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(items[0]).Length < parameters.OutputCaptureMaxBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(items).Length > parameters.OutputCaptureMaxBytes);

        var seen = new List<string>();
        var writer = Writer(parameters, seen);
        var error = await Assert.ThrowsAsync<UpstreamException>(() =>
            writer.WriteLinesAsync(Chunks(events), _ => false, () => 0));

        Assert.Contains("MaxCaptureBytes", error.Message);
        Assert.InRange(seen.Count, 1, parameters.OutputItemCount - 1);
        Assert.DoesNotContain("response.completed", seen);
        Assert.Null(writer.Result);
    }

    [Fact]
    public async Task TerminalOwnOutputExceedsCaptureBytesEvenWhenTheSingleEventFits()
    {
        var parameters = OutputParameters.Load();
        var terminal = Terminal([Message(new string('x', checked(parameters.OutputDeltaCharacters * parameters.OutputItemCount)))]);
        var response = (Dictionary<string, object?>)terminal["response"]!;
        var wire = Event(terminal);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= parameters.OutputEventMaxBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response).Length > parameters.OutputCaptureMaxBytes);

        var seen = new List<string>();
        var writer = Writer(parameters, seen);
        var error = await Assert.ThrowsAsync<UpstreamException>(() =>
            writer.WriteLinesAsync(Chunks(wire), _ => false, () => 0));

        Assert.Contains("MaxCaptureBytes", error.Message);
        Assert.Empty(seen);
        Assert.Null(writer.Result);
    }

    [Theory]
    [InlineData("MaxCaptureItems")]
    [InlineData("MaxCaptureFields")]
    public async Task TinyTerminalOutputStillHonorsTheConfiguredStructureLimit(string budgetName)
    {
        var parameters = OutputParameters.Load();
        List<object?> output;
        if (budgetName == "MaxCaptureItems")
        {
            output = Enumerable.Range(0, checked(parameters.OutputItemCount + 1))
                .Select(_ => (object?)new Dictionary<string, object?> { ["type"] = "message" })
                .ToList();
        }
        else
        {
            var item = new Dictionary<string, object?> { ["type"] = "message" };
            for (var index = 0; index <= parameters.OutputItemCount; index++)
                item[$"field_{index}"] = "x";
            output = [item];
        }
        var terminal = Terminal(output);
        var wire = Event(terminal);
        Assert.True(Encoding.UTF8.GetByteCount(wire) <= parameters.OutputEventMaxBytes);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(terminal["response"]).Length < parameters.OutputCaptureMaxBytes);

        var seen = new List<string>();
        var writer = Writer(parameters, seen, budgetName);
        var error = await Assert.ThrowsAsync<UpstreamException>(() =>
            writer.WriteLinesAsync(Chunks(wire), _ => false, () => 0));

        Assert.Contains(budgetName, error.Message);
        Assert.Empty(seen);
        Assert.Null(writer.Result);
    }

    private static MultiAgentModelStreamWriter Writer(OutputParameters parameters, List<string> seen, string? structureLimit = null) =>
        new((payload, _) =>
        {
            seen.Add(JsonDictionaryValue.String(payload, "type"));
            return Task.CompletedTask;
        }, new SseFrameOptions
        {
            MaxEventBytes = parameters.OutputEventMaxBytes,
            MaxCaptureBytes = parameters.OutputCaptureMaxBytes,
            MaxCaptureItems = structureLimit == "MaxCaptureItems"
                ? parameters.OutputItemCount
                : ProtocolConversionParameters.Current.SseMaxCaptureItems,
            MaxCaptureFields = structureLimit == "MaxCaptureFields"
                ? parameters.OutputItemCount
                : ProtocolConversionParameters.Current.SseMaxCaptureFields
        });

    private static Dictionary<string, object?> Message(string text) => new()
    {
        ["type"] = "message",
        ["content"] = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = text }
        }
    };

    private static Dictionary<string, object?> Terminal(List<object?> output) => new()
    {
        ["type"] = "response.completed",
        ["response"] = new Dictionary<string, object?>
        {
            ["id"] = "resp_capture_limit",
            ["status"] = "completed",
            ["output"] = output
        }
    };

    private static string Event(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static async IAsyncEnumerable<string> Chunks(params string[] values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.CompletedTask;
        }
    }

    private sealed record OutputParameters(
        int OutputEventMaxBytes,
        int OutputCaptureMaxBytes,
        int OutputDeltaCharacters,
        int OutputItemCount)
    {
        public static OutputParameters Load()
        {
            return new(WsHistoryTestParameters.OutputEventMaxBytes, WsHistoryTestParameters.OutputCaptureMaxBytes,
                WsHistoryTestParameters.OutputDeltaCharacters, WsHistoryTestParameters.OutputItemCount);
        }
    }
}
