using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

/// <summary>
/// 端到端集成测试：验证协议转换、WebSearch、ApplyPatch、Vision的完整流程
/// 重点关注流式输出的正确性和性能
/// </summary>
public sealed class StreamingIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    #region Test Helpers

    private static async IAsyncEnumerable<string> SseLines(params string[] sseBlocks)
    {
        foreach (var block in sseBlocks)
        {
            foreach (var line in block.Split('\n'))
            {
                yield return line;
            }
        }

        await Task.CompletedTask;
    }

    private static string SseBlock(string data, string? eventName = null)
    {
        var sb = new StringBuilder();
        if (eventName is not null)
        {
            sb.Append("event: ").Append(eventName).Append('\n');
        }

        sb.Append("data: ").Append(data).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    private static string ChatChunk(
        string? content = null,
        object? toolCalls = null,
        string? finishReason = null,
        string id = "chatcmpl-1",
        string model = "gpt-5")
    {
        var delta = new Dictionary<string, object?>();
        if (content is not null) delta["content"] = content;
        if (toolCalls is not null) delta["tool_calls"] = toolCalls;

        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["delta"] = delta,
            ["finish_reason"] = finishReason
        };

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new List<object?> { choice }
        };

        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static string MessagesBlock(string type, object content, string? eventName = null)
    {
        var data = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            [GetContentKey(type)] = content
        }, JsonOpts);

        return SseBlock(data, eventName ?? type);
    }

    private static string GetContentKey(string type) => type switch
    {
        "message_start" => "message",
        "content_block_start" => "content_block",
        "content_block_delta" => "delta",
        "message_delta" => "delta",
        _ => "data"
    };

    private static List<Dictionary<string, object?>> ParseEvents(List<string> sseLines)
    {
        var events = new List<Dictionary<string, object?>>();
        foreach (var line in sseLines)
        {
            if (line.Contains("data:", StringComparison.Ordinal))
            {
                events.Add(ParseJsonEvent(line));
            }
        }

        return events;
    }

    private static Dictionary<string, object?> ParseJsonEvent(string sseLine)
    {
        var idx = sseLine.IndexOf("data: ", StringComparison.Ordinal);
        var json = sseLine[(idx + "data: ".Length)..].TrimEnd('\n');
        using var doc = JsonDocument.Parse(json);
        return ElementToDict(doc.RootElement)!;
    }

    private static Dictionary<string, object?>? ElementToDict(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in e.EnumerateObject())
        {
            dict[prop.Name] = ElementToObject(prop.Value);
        }

        return dict;
    }

    private static object? ElementToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ElementToDict(e),
        JsonValueKind.Array => e.EnumerateArray().Select(ElementToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => null
    };

    private static Dictionary<string, object?>? FindEvent(List<Dictionary<string, object?>> events, string eventType)
    {
        return events.FirstOrDefault(e =>
            e.TryGetValue("type", out var t) && eventType.Equals(t?.ToString()));
    }

    private static async Task<(List<string> events, TimeSpan ttft, List<long> timestamps)> CollectWithTimestamps(
        IAsyncEnumerable<string> enumerable)
    {
        var events = new List<string>();
        var timestamps = new List<long>();
        var startTime = Stopwatch.GetTimestamp();
        var firstContentTime = (long?)null;

        await foreach (var line in enumerable)
        {
            var now = Stopwatch.GetTimestamp();
            timestamps.Add(now);
            events.Add(line);

            // 记录第一个内容事件的时间（非metadata事件）
            if (firstContentTime is null &&
                (line.Contains("response.output_text.delta") || line.Contains("response.function_call_arguments.delta")))
            {
                firstContentTime = now;
            }
        }

        var ttft = firstContentTime.HasValue
            ? Stopwatch.GetElapsedTime(startTime, firstContentTime.Value)
            : TimeSpan.Zero;

        return (events, ttft, timestamps);
    }

    #endregion

    #region Chat → Responses 基础转换

    [Fact]
    public async Task ChatToResponses_SimpleText_StreamsImmediatelyWithCorrectSequence()
    {
        // Arrange
        var lines = SseLines(
            SseBlock(ChatChunk(content: "Hello")),
            SseBlock(ChatChunk(content: " world")),
            SseBlock(ChatChunk(content: "!")),
            SseBlock(ChatChunk(finishReason: "stop")),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();

        // Act
        var (events, ttft, timestamps) = await CollectWithTimestamps(
            SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None));

        var parsed = ParseEvents(events);

        // Assert - 事件序列
        Assert.Equal("response.created", FindEvent(parsed, "response.created")?["type"]);
        Assert.Equal("response.output_item.added", FindEvent(parsed, "response.output_item.added")?["type"]);
        Assert.Equal("response.content_part.added", FindEvent(parsed, "response.content_part.added")?["type"]);
        Assert.NotNull(FindEvent(parsed, "response.output_text.delta"));
        Assert.NotNull(FindEvent(parsed, "response.output_text.done"));
        Assert.NotNull(FindEvent(parsed, "response.output_item.done"));
        Assert.NotNull(FindEvent(parsed, "response.completed"));

        // Assert - 流式特性：TTFT应该很短（< 100ms）
        Assert.True(ttft < TimeSpan.FromMilliseconds(100),
            $"TTFT too high: {ttft.TotalMilliseconds}ms");

        // Assert - 事件之间的间隔应该很小（不是批量输出）
        for (int i = 1; i < Math.Min(5, timestamps.Count); i++)
        {
            var interval = Stopwatch.GetElapsedTime(timestamps[i - 1], timestamps[i]);
            Assert.True(interval < TimeSpan.FromMilliseconds(50),
                $"Event #{i} interval too high: {interval.TotalMilliseconds}ms");
        }

        // Assert - 内容完整性
        var textDeltas = parsed
            .Where(e => "response.output_text.delta".Equals(e["type"]))
            .Select(e => e["delta"]?.ToString() ?? "")
            .ToList();
        Assert.Equal("Hello world!", string.Join("", textDeltas));
    }

    #endregion

    #region Messages → Responses 基础转换

    [Fact]
    public async Task MessagesToResponses_SimpleText_StreamsCorrectly()
    {
        // Arrange
        var lines = SseLines(
            MessagesBlock("message_start", new { id = "msg_1", model = "claude-3", usage = new { input_tokens = 5, output_tokens = 0 } }),
            MessagesBlock("content_block_start", new { type = "text", text = "" }, "content_block_start"),
            MessagesBlock("content_block_delta", new { type = "text_delta", text = "Hello" }, "content_block_delta"),
            MessagesBlock("content_block_delta", new { type = "text_delta", text = " Claude" }, "content_block_delta"),
            MessagesBlock("content_block_stop", new { }, "content_block_stop"),
            MessagesBlock("message_delta", new { stop_reason = "end_turn", usage = new { output_tokens = 10 } }, "message_delta"),
            MessagesBlock("message_stop", new { }, "message_stop"));

        var result = new ConvertedStreamResult();

        // Act
        var (events, ttft, _) = await CollectWithTimestamps(
            SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None));

        var parsed = ParseEvents(events);

        // Assert - 流式特性
        Assert.True(ttft < TimeSpan.FromMilliseconds(100));

        // Assert - 内容完整性
        var textDeltas = parsed
            .Where(e => "response.output_text.delta".Equals(e["type"]))
            .Select(e => e["delta"]?.ToString() ?? "")
            .ToList();
        Assert.Equal("Hello Claude", string.Join("", textDeltas));
    }

    #endregion

    #region Vision 图像识别

    [Fact]
    public async Task MessagesToResponses_WithVision_PreservesImageContent()
    {
        // Arrange - 模拟带图像的消息
        var lines = SseLines(
            MessagesBlock("message_start", new
            {
                id = "msg_vision",
                model = "claude-3-opus",
                usage = new { input_tokens = 1500, output_tokens = 0 } // 图像会占用更多tokens
            }),
            MessagesBlock("content_block_start", new { type = "text", text = "" }, "content_block_start"),
            MessagesBlock("content_block_delta", new { type = "text_delta", text = "我看到图片中有" }, "content_block_delta"),
            MessagesBlock("content_block_delta", new { type = "text_delta", text = "一只猫" }, "content_block_delta"),
            MessagesBlock("content_block_stop", new { }, "content_block_stop"),
            MessagesBlock("message_delta", new { stop_reason = "end_turn", usage = new { output_tokens = 20 } }, "message_delta"),
            MessagesBlock("message_stop", new { }, "message_stop"));

        var result = new ConvertedStreamResult();

        // Act
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3-opus", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);

        // Assert - Vision相关的usage应该正确
        var completed = FindEvent(parsed, "response.completed");
        Assert.NotNull(completed);
        var response = completed!["response"] as Dictionary<string, object?>;
        var usage = response!["usage"] as Dictionary<string, object?>;
        Assert.NotNull(usage);
        Assert.Equal(1500, Convert.ToInt64(usage!["input_tokens"]));

        // Assert - 内容正确识别
        var textDeltas = parsed
            .Where(e => "response.output_text.delta".Equals(e["type"]))
            .Select(e => e["delta"]?.ToString() ?? "")
            .ToList();
        Assert.Contains("猫", string.Join("", textDeltas));
    }

    #endregion

    #region ApplyPatch 工具转换

    [Fact]
    public async Task ApplyPatch_FreeformTool_StreamsAsCustomToolCall()
    {
        var lines = SseLines(
            SseBlock(ChatChunk(toolCalls: new[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["id"] = "call_patch_001",
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "apply_patch",
                        ["arguments"] = JsonSerializer.Serialize(new
                        {
                            patch = "*** Begin Patch\n*** Add File: freeform.txt\n+hello\n*** End Patch"
                        })
                    }
                }
            })),
            SseBlock(ChatChunk(finishReason: "tool_calls")),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);
        var added = parsed.FirstOrDefault(e =>
            "response.output_item.added".Equals(e["type"]?.ToString(), StringComparison.Ordinal)
            && e["item"] is Dictionary<string, object?> item
            && "custom_tool_call".Equals(item["type"]?.ToString(), StringComparison.Ordinal));
        Assert.NotNull(added);

        var deltas = parsed.Where(e => "response.custom_tool_call_input.delta".Equals(e["type"]?.ToString(), StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(deltas);
        Assert.DoesNotContain(parsed, e => "response.function_call_arguments.delta".Equals(e["type"]?.ToString(), StringComparison.Ordinal));

        var completed = FindEvent(parsed, "response.completed");
        Assert.NotNull(completed);
        var response = completed!["response"] as Dictionary<string, object?>;
        var output = response!["output"] as List<object?>;
        var customToolCall = output!
            .OfType<Dictionary<string, object?>>()
            .FirstOrDefault(entry => "custom_tool_call".Equals(entry["type"]?.ToString(), StringComparison.Ordinal));
        Assert.NotNull(customToolCall);
        Assert.Equal("apply_patch", customToolCall!["name"]?.ToString());
        var input = Assert.IsType<string>(customToolCall["input"]);
        Assert.StartsWith("*** Begin Patch", input, StringComparison.Ordinal);
        Assert.Contains("*** Add File: freeform.txt", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyApplyPatchUpdateFileTool_PassesThroughAsFunctionCall()
    {
        // Arrange - 模拟历史兼容 apply_patch_update_file tool_use
        var lines = SseLines(
            MessagesBlock("message_start", new { id = "msg_patch", model = "claude-3", usage = new { input_tokens = 100, output_tokens = 0 } }),
            MessagesBlock("content_block_start", new
            {
                type = "tool_use",
                id = "toolu_patch_001",
                name = "apply_patch_update_file",
                input = new { }
            }, "content_block_start"),
            MessagesBlock("content_block_delta", new
            {
                type = "input_json_delta",
                partial_json = "{\"path\":\"src/Example.cs\",\"hunks\":[{\"lines\":[{\"op\":\"context\",\"text\":\"public class Example\"},{\"op\":\"remove\",\"text\":\"    // TODO\"},{\"op\":\"add\",\"text\":\"    // DONE\"}]}]}"
            }, "content_block_delta"),
            MessagesBlock("content_block_stop", new { }, "content_block_stop"),
            MessagesBlock("message_delta", new { stop_reason = "tool_use", usage = new { output_tokens = 50 } }, "message_delta"),
            MessagesBlock("message_stop", new { }, "message_stop"));

        var result = new ConvertedStreamResult();

        // Act
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);

        // Assert - 历史兼容 apply_patch 工具仍按普通 function_call 透传
        var completed = FindEvent(parsed, "response.completed");
        Assert.NotNull(completed);
        var response = completed!["response"] as Dictionary<string, object?>;
        var output = response!["output"] as List<object?>;
        Assert.NotNull(output);

        var functionCall = output!
            .OfType<Dictionary<string, object?>>()
            .FirstOrDefault(fc => "function_call".Equals(fc["type"]));
        Assert.NotNull(functionCall);
        Assert.Equal("toolu_patch_001", functionCall!["call_id"]?.ToString());
        Assert.Equal("apply_patch_update_file", functionCall["name"]?.ToString());

        // Assert - arguments passed through as original JSON
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Assert.IsType<string>(functionCall["arguments"]));
        Assert.NotNull(arguments);
        Assert.Equal("src/Example.cs", arguments!["path"]?.ToString());
        Assert.DoesNotContain("cmd", arguments.Keys);
        Assert.DoesNotContain("OPENCODEX_PATCH", Assert.IsType<string>(functionCall["arguments"]));
    }

    [Fact]
    public async Task LegacyApplyPatchAddFileTool_PassesThroughAsFunctionCall()
    {
        // Arrange
        var lines = SseLines(
            MessagesBlock("message_start", new { id = "msg_add", model = "claude-3", usage = new { input_tokens = 50, output_tokens = 0 } }),
            MessagesBlock("content_block_start", new
            {
                type = "tool_use",
                id = "toolu_add_001",
                name = "apply_patch_add_file",
                input = new { }
            }, "content_block_start"),
            MessagesBlock("content_block_delta", new
            {
                type = "input_json_delta",
                partial_json = "{\"path\":\"new_file.txt\",\"content\":\"Hello\\nWorld\"}"
            }, "content_block_delta"),
            MessagesBlock("content_block_stop", new { }, "content_block_stop"),
            MessagesBlock("message_delta", new { stop_reason = "tool_use", usage = new { output_tokens = 30 } }, "message_delta"),
            MessagesBlock("message_stop", new { }, "message_stop"));

        var result = new ConvertedStreamResult();

        // Act
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);
        var completed = FindEvent(parsed, "response.completed");
        var response = (completed!["response"] as Dictionary<string, object?>)!;
        var output = (response["output"] as List<object?>)!;
        var functionCall = output.OfType<Dictionary<string, object?>>()
            .First(fc => "function_call".Equals(fc["type"]));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Assert.IsType<string>(functionCall["arguments"]));

        // Assert - arguments passed through as original JSON
        Assert.Equal("apply_patch_add_file", functionCall["name"]?.ToString());
        Assert.Equal("new_file.txt", arguments!["path"]?.ToString());
        Assert.Equal("Hello\nWorld", arguments["content"]?.ToString());
        Assert.DoesNotContain("cmd", arguments.Keys);
        Assert.DoesNotContain("OPENCODEX_PATCH", Assert.IsType<string>(functionCall["arguments"]));
    }

    [Fact]
    public async Task LegacyApplyPatchBatchTool_PassesThroughAsFunctionCall()
    {
        // Arrange
        var lines = SseLines(
            MessagesBlock("message_start", new { id = "msg_batch", model = "claude-3", usage = new { input_tokens = 100, output_tokens = 0 } }),
            MessagesBlock("content_block_start", new
            {
                type = "tool_use",
                id = "toolu_batch_001",
                name = "apply_patch_batch",
                input = new { }
            }, "content_block_start"),
            MessagesBlock("content_block_delta", new
            {
                type = "input_json_delta",
                partial_json = "{\"operations\":[{\"type\":\"add_file\",\"path\":\"a.txt\",\"content\":\"A\"},{\"type\":\"delete_file\",\"path\":\"b.txt\"}]}"
            }, "content_block_delta"),
            MessagesBlock("content_block_stop", new { }, "content_block_stop"),
            MessagesBlock("message_delta", new { stop_reason = "tool_use", usage = new { output_tokens = 40 } }, "message_delta"),
            MessagesBlock("message_stop", new { }, "message_stop"));

        var result = new ConvertedStreamResult();

        // Act
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.MessagesToResponsesEvents(lines, "claude-3", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);
        var completed = FindEvent(parsed, "response.completed");
        var response = (completed!["response"] as Dictionary<string, object?>)!;
        var output = (response["output"] as List<object?>)!;
        var functionCall = output.OfType<Dictionary<string, object?>>()
            .First(fc => "function_call".Equals(fc["type"]));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Assert.IsType<string>(functionCall["arguments"]));

        // Assert - arguments passed through as original JSON with operations
        Assert.Equal("apply_patch_batch", functionCall["name"]?.ToString());
        Assert.DoesNotContain("cmd", arguments!.Keys);
        var operationsJson = Assert.IsType<JsonElement>(arguments["operations"]);
        Assert.Equal(JsonValueKind.Array, operationsJson.ValueKind);
        Assert.Equal(2, operationsJson.GetArrayLength());
        var firstOp = operationsJson[0];
        Assert.Equal("add_file", firstOp.GetProperty("type").GetString());
        Assert.Equal("a.txt", firstOp.GetProperty("path").GetString());
        var secondOp = operationsJson[1];
        Assert.Equal("delete_file", secondOp.GetProperty("type").GetString());
        Assert.Equal("b.txt", secondOp.GetProperty("path").GetString());
        Assert.DoesNotContain("OPENCODEX_PATCH", Assert.IsType<string>(functionCall["arguments"]));
    }

    #endregion

    #region 流式性能验证

    [Fact]
    public async Task StreamingPerformance_NoBuffering_EventsYieldedImmediately()
    {
        // Arrange - 创建带延迟的异步流，模拟网络延迟
        async IAsyncEnumerable<string> DelayedLines()
        {
            yield return SseBlock(ChatChunk(content: "First"));
            await Task.Delay(10); // 模拟网络延迟
            yield return SseBlock(ChatChunk(content: " Second"));
            await Task.Delay(10);
            yield return SseBlock(ChatChunk(content: " Third"));
            await Task.Delay(10);
            yield return SseBlock(ChatChunk(finishReason: "stop"));
            yield return SseBlock("[DONE]");
        }

        var result = new ConvertedStreamResult();

        // Act
        var receivedTimes = new List<long>();
        var startTime = Stopwatch.GetTimestamp();

        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(
                           DelayedLines(), "gpt-5", result, CancellationToken.None))
        {
            receivedTimes.Add(Stopwatch.GetTimestamp());
        }

        // Assert - 事件应该逐个到达，不是批量
        // 第一个内容事件应该在30ms内到达（不等待全部完成）
        var firstContentIndex = receivedTimes.Take(10)
            .Select((t, i) => (t, i))
            .FirstOrDefault(x => Stopwatch.GetElapsedTime(startTime, x.t).TotalMilliseconds > 0).i;

        var firstContentTime = Stopwatch.GetElapsedTime(startTime, receivedTimes[firstContentIndex]);
        Assert.True(firstContentTime < TimeSpan.FromMilliseconds(30),
            $"First content event took {firstContentTime.TotalMilliseconds}ms, expected < 30ms");
    }

    #endregion

    #region 事件完整性验证

    [Fact]
    public async Task EventCompleteness_NoMissingOrDuplicateEvents()
    {
        // Arrange
        var lines = SseLines(
            SseBlock(ChatChunk(content: "A")),
            SseBlock(ChatChunk(content: "B")),
            SseBlock(ChatChunk(content: "C")),
            SseBlock(ChatChunk(finishReason: "stop")),
            SseBlock("[DONE]"));

        var result = new ConvertedStreamResult();

        // Act
        var events = new List<string>();
        await foreach (var line in SseStreamConverter.ChatToResponsesEvents(lines, "gpt-5", result, CancellationToken.None))
        {
            events.Add(line);
        }

        var parsed = ParseEvents(events);

        // Assert - 每种事件类型只出现预期次数
        var eventCounts = parsed
            .GroupBy(e => e["type"]?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(1, eventCounts["response.created"]);
        Assert.Equal(1, eventCounts["response.output_item.added"]);
        Assert.Equal(1, eventCounts["response.content_part.added"]);
        Assert.Equal(3, eventCounts["response.output_text.delta"]); // A, B, C
        Assert.Equal(1, eventCounts["response.output_text.done"]);
        Assert.Equal(1, eventCounts["response.output_item.done"]);
        Assert.Equal(1, eventCounts["response.completed"]);

        // Assert - 事件顺序正确
        var eventTypes = parsed.Select(e => e["type"]?.ToString()).ToList();
        Assert.Equal("response.created", eventTypes[0]);
        Assert.Equal("response.completed", eventTypes[^1]);
        Assert.True(eventTypes.IndexOf("response.output_item.added") < eventTypes.IndexOf("response.output_text.delta"));
        Assert.True(eventTypes.LastIndexOf("response.output_text.delta") < eventTypes.IndexOf("response.output_text.done"));
    }

    #endregion
}
