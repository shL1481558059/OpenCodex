using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.Proxy;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.CoreBase.Domain.WebSearch;
using OpenCodex.CoreBase.DTOs.Proxy;
using OpenCodex.CoreBase.Services.Proxy;
using OpenCodex.CoreBase.Services.WebSearch;
using Xunit;
using static OpenCodex.CoreBase.Abstractions.WebSearchPayload;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAndSearchingEvents_ArriveBeforeSearchReturns(bool fail)
    {
        var executor = new BlockedSearchExecutor(fail);
        var writer = new RecordingWriter();
        var history = WebSearchTestStore.Create();
        var payload = new Dictionary<string, object?> { ["input"] = "search" };
        var channel = new Dictionary<string, object?> { ["type"] = "chat", ["id"] = "test" };
        var context = new ProxyStreamContext(
            Stopwatch.GetTimestamp(), Guid.NewGuid(), "progress", "admin", null, payload, payload,
            new Dictionary<string, object?>
            {
                ["model"] = "upstream", ["messages"] = new List<object?>(),
                ["tools"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?> { ["name"] = WebSearchRequestPolicy.InternalToolName }
                    }
                }
            },
            "responses", new ProxyRouteDto(channel, "public", "upstream", false, true),
            "chat", "test", "superadmin", "upstream", "public", 30,
            new ProxyRequestMetadata("POST", "/v1/responses", null, new Dictionary<string, string>()),
            writer, CancellationToken.None)
        {
            BuiltinTools = new BuiltinToolRequestContext
            {
                OwnerUserId = WebSearchTestStore.OwnerUserId,
                WebSearchToolName = WebSearchRequestPolicy.InternalToolName,
                MaxWebSearchCalls = 1
            }
        };
        var service = new ProxyStreamService(new ChatStream(), new Logs(), executor, history);
        var task = service.StreamAsync(context);
        try
        {
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = writer.Events.ToArray();
            var added = Assert.Single(before, item => StringValue(item, "type") == "response.output_item.added"
                && StringValue(ObjectValue(item, "item"), "type") == "web_search_call");
            var search = ObjectValue(added, "item");
            Assert.Equal("OpenAI Responses tools", ObjectValue(search, "action")["query"]);
            var progress = Assert.Single(before, item => StringValue(item, "type") == "response.web_search_call.in_progress");
            var searching = Assert.Single(before, item => StringValue(item, "type") == "response.web_search_call.searching");
            Assert.Equal(search["id"], progress["item_id"]);
            Assert.Equal(search["id"], searching["item_id"]);
            Assert.Equal(added["output_index"], searching["output_index"]);
            Assert.False(task.IsCompleted);
            Assert.DoesNotContain(before, item => StringValue(item, "type") == "response.web_search_call.completed");
        }
        finally
        {
            executor.Release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var events = writer.Events.ToArray();
        var completed = events.Where(item => StringValue(item, "type") == "response.web_search_call.completed").ToList();
        Assert.Equal(fail ? 0 : 1, completed.Count);
        var done = Assert.Single(events, item => StringValue(item, "type") == "response.output_item.done"
            && StringValue(ObjectValue(item, "item"), "type") == "web_search_call");
        Assert.Equal(fail ? "failed" : "completed", ObjectValue(done, "item")["status"]);
        var sequence = events.Select(item => ToInt(GetValue(item, "sequence_number"), -1)).Where(number => number >= 0).ToList();
        Assert.True(sequence.Zip(sequence.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.Single(events, item => StringValue(item, "type") == "response.completed");
    }

    private sealed class BlockedSearchExecutor(bool fail) : IWebSearchToolExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CurrentMode() => WebSearchModes.Simulate;
        public async Task<WebSearchToolResult> ExecuteAsync(string callId, string arguments, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (fail) return WebSearchToolResult.Failed(callId, "OpenAI Responses tools", "provider unavailable", true);
            var result = new Dictionary<string, object?> { ["answer"] = "source", ["results"] = new List<object?>() };
            return new WebSearchToolResult(callId, "OpenAI Responses tools", "completed", JsonDumps(result), result,
                null, "test", null, null, null, null, null, 200, null);
        }
    }

    private sealed class ChatStream : IUpstreamClient
    {
        private int _calls;
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel, IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var first = ++_calls == 1;
            var delta = first
                ? new Dictionary<string, object?>
                {
                    ["tool_calls"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["index"] = 0, ["id"] = "search_1", ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = WebSearchRequestPolicy.InternalToolName,
                                ["arguments"] = "{\"query\":\"OpenAI Responses tools\"}"
                            }
                        }
                    }
                }
                : new Dictionary<string, object?> { ["content"] = "answer" };
            yield return "data: " + JsonSerializer.Serialize(new { id = "chat", model = "upstream", choices = new[] { new { index = 0, delta } } });
            yield return "";
            yield return "data: " + JsonSerializer.Serialize(new { id = "chat", model = "upstream", choices = new[] { new { index = 0, delta = new { }, finish_reason = first ? "tool_calls" : "stop" } } });
            yield return "";
            yield return "data: [DONE]";
            yield return "";
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingWriter : IProxyStreamWriter
    {
        public ConcurrentQueue<Dictionary<string, object?>> Events { get; } = new();
        public void PrepareSse() { }
        public async Task<StreamWriteMetrics> WriteLinesAsync(
            IAsyncEnumerable<string> lines, Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds,
            CancellationToken cancellationToken = default)
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
                if (WebSearchResponsePayload.ParseSseLine(line).Payload is { } value) Events.Enqueue(value);
            return new StreamWriteMetrics();
        }
    }

    private sealed class Logs : IProxyLogService
    {
        public Guid CreateQueuedLog(ProxyRequestLogQueuedContext context) => Guid.NewGuid();
        public void MarkProcessing(Guid id, ProxyRequestLogProcessingContext context) { }
        public Task CompleteLogAsync(Guid id, ProxyLogContext context, ProxyRequestMetadata request) => Task.CompletedTask;
        public Task<Guid> WriteLogAsync(ProxyLogContext context, ProxyRequestMetadata request) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> WriteLogAsync(ProxyRequestLogContext context) => Task.FromResult(Guid.NewGuid());
    }
}
