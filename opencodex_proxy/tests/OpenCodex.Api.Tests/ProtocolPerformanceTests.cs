using System.Diagnostics;
using System.Text.Json;
using OpenCodex.CoreBase.Domain.Proxy;
using OpenCodex.ProtocolConversion;
using OpenCodex.ProtocolConversion.Models;
using OpenCodex.ProtocolConversion.Streaming;
using Xunit;
using Xunit.Abstractions;
using D = System.Collections.Generic.Dictionary<string, object?>;

namespace OpenCodex.Api.Tests;

[CollectionDefinition("Protocol performance", DisableParallelization = true)]
public sealed class ProtocolPerformanceCollection;

[Collection("Protocol performance")]
public sealed class ProtocolPerformanceTests(ITestOutputHelper log)
{
    private static readonly Parameters Settings = JsonSerializer.Deserialize<Parameters>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "protocol-performance.parameters.json")))!;

    public static IEnumerable<object[]> TextAccumulationCases =>
        Settings.TextCharacterCounts.SelectMany(characters =>
            new[] { new object[] { false, characters }, new object[] { true, characters } });

    [Fact]
    public void GeneratedFramesDoNotAllocateTemporaryEncodingArrays()
    {
        var payload = new D { ["delta"] = new string('x', Settings.GeneratedDeltaCharacters) };
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        Write();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Settings.GeneratedFrames; index++) Write();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"Generated frames: count={Settings.GeneratedFrames}, allocated={allocated}");
        Assert.True(allocated < Settings.MaximumGeneratedFrameAllocatedBytes,
            $"Generated frames allocated {allocated} bytes.");

        void Write()
        {
            output.Clear();
            var frame = SseFrame.FromOwnedJson(payload);
            Assert.Equal(frame.Utf8Length, frame.WriteUtf8(output));
            Assert.False(frame.IsWireMaterialized);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SseReaderAllocationIncludesOnlyParsingAndInputChunks(bool multiline)
    {
        var text = new string('x', Settings.TextCharacters);
        var data = JsonSerializer.Serialize(new D { ["delta"] = text });
        var wire = multiline ? "data: {\ndata: " + data[1..] + "\n\n" : "data: " + data + "\n\n";
        _ = Read();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var frame = Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"SSE reader: multiline={multiline}, wireCharacters={wire.Length}, allocated={allocated}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}");
        Assert.Equal(wire, frame.WireText);
        Assert.Equal(text, Assert.IsType<D>(frame.Event!.Data)["delta"]);
        Assert.True(allocated < Settings.MaximumSseReaderAllocatedBytes,
            $"SSE reader allocated {allocated} bytes.");

        SseFrame Read()
        {
            var parser = new SseFrameReader();
            SseFrame? result = null;
            for (var offset = 0; offset < wire.Length; offset += Settings.SseReaderChunkCharacters)
            {
                var chunk = wire.Substring(offset, Math.Min(Settings.SseReaderChunkCharacters, wire.Length - offset));
                foreach (var item in parser.Feed(chunk)) result = item;
            }
            return result!;
        }
    }

    [Theory]
    [MemberData(nameof(TextAccumulationCases))]
    public async Task MessagesTextAccumulationUsesLinearMemory(bool shared, int characters)
    {
        var folder = Path.Combine(Path.GetTempPath(), "protocol-linear-" + Guid.NewGuid().ToString("N"));
        var spool = new ProxyAuditSpool(new() { Directory = folder });
        try
        {
            await Convert(Settings.DeltaCharacters);
            var before = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            await Convert(characters);
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            log.WriteLine($"Messages->Responses: shared={shared}, characters={characters}, allocated={allocated}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}");
            var budget = (long)Settings.MaximumTextAllocatedBytes * characters / Settings.TextCharacters;
            Assert.True(allocated < budget,
                $"Text accumulation allocated {allocated} bytes.");
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        async Task Convert(int characters)
        {
            using var capture = shared
                ? new StreamResponseCapture("messages", new StreamCaptureBudget(
                    Settings.MaximumTextAllocatedBytes, int.MaxValue, int.MaxValue, spool, default))
                : null;
            var result = new ConvertedStreamResult { SharedCapture = capture };
            await foreach (var _ in SseStreamConverter.MessagesToResponsesFrames(
                TextEvents(characters, Settings.DeltaCharacters, capture), "public", result, null, false, 0, 0, default))
            {
            }
            var blocks = Assert.IsType<List<object?>>(result.UpstreamResponse!["content"]);
            var block = Assert.IsType<D>(Assert.Single(blocks));
            Assert.Equal(characters, Assert.IsType<string>(block["text"]).Length);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ThinkingBlocksPreserveOrderAndSignatures(bool shared, bool textDelta)
    {
        using var document = JsonDocument.Parse("""
            [
              {"type":"message_start","message":{"id":"m","content":[]}},
              {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"a","signature":"s0"}},
              {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"b"}},
              {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"s1"}},
              {"type":"content_block_stop","index":0},
              {"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"opaque"}},
              {"type":"content_block_stop","index":1},
              {"type":"content_block_start","index":2,"content_block":{"type":"thinking","thinking":"c","signature":"s2"}},
              {"type":"content_block_delta","index":2,"delta":{"type":"thinking_delta","thinking":"d"}},
              {"type":"content_block_stop","index":2},
              {"type":"content_block_start","index":3,"content_block":{"type":"text","text":"hello"}},
              {"type":"content_block_delta","index":3,"delta":{"type":"text_delta","text":" world"}},
              {"type":"content_block_stop","index":3},
              {"type":"message_delta","delta":{"stop_reason":"end_turn"}},
              {"type":"message_stop"}
            ]
            """);
        var input = (List<object?>)ProtocolJson.FromJsonElement(document.RootElement)!;
        if (textDelta)
        {
            foreach (var item in input.OfType<D>().Where(item => item.GetValueOrDefault("delta") is D))
            {
                var delta = (D)item["delta"]!;
                if (delta.GetValueOrDefault("type") is not "thinking_delta") continue;
                delta["type"] = "text_delta";
                delta["text"] = delta["thinking"];
                delta.Remove("thinking");
            }
        }
        var folder = Path.Combine(Path.GetTempPath(), "protocol-thinking-" + Guid.NewGuid().ToString("N"));
        var spool = new ProxyAuditSpool(new() { Directory = folder });
        try
        {
            using var capture = shared ? new StreamResponseCapture("messages",
                new StreamCaptureBudget(Settings.MaximumTextAllocatedBytes, int.MaxValue, int.MaxValue, spool, default)) : null;
            var result = new ConvertedStreamResult { SharedCapture = capture };
            D? reasoning = null;
            await foreach (var frame in SseStreamConverter.MessagesToResponsesFrames(
                Events(), "public", result, null, false, 0, 0, default))
            {
                if (frame.Type == "response.output_item.done"
                    && frame.Event!.Data is D payload && payload["item"] is D item
                    && item["type"] is "reasoning") reasoning = item;
            }
            Assert.NotNull(reasoning);
            Assert.True(ProtocolConverter.TryDecodeAnthropicThinkingBlocks(
                (string)reasoning["encrypted_content"]!, out var blocks));
            Assert.Equal(3, blocks.Count);
            Assert.Equal("ab", ((D)blocks[0]!)["thinking"]);
            Assert.Equal("s0s1", ((D)blocks[0]!)["signature"]);
            Assert.Equal("opaque", ((D)blocks[1]!)["data"]);
            Assert.Equal("cd", ((D)blocks[2]!)["thinking"]);
            Assert.Equal("s2", ((D)blocks[2]!)["signature"]);
            var content = (List<object?>)result.UpstreamResponse!["content"]!;
            Assert.Equal("hello world", ((D)content[3]!)["text"]);

            async IAsyncEnumerable<SseEvent> Events()
            {
                foreach (var item in input.OfType<D>())
                {
                    var value = new SseEvent((string)item["type"]!, item);
                    capture?.Accept(new SseFrame("", value));
                    yield return value;
                }
                await Task.CompletedTask;
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ChatSchemaSanitizationBuildsOneDetachedGraph()
    {
        var description = new string('d', Settings.DescriptionCharacters);
        var tools = Enumerable.Range(0, Settings.SchemaTools).Select(index => (object?)new D
        {
            ["type"] = "function",
            ["function"] = new D
            {
                ["name"] = "tool_" + index,
                ["parameters"] = new D
                {
                    ["type"] = "object",
                    ["properties"] = Enumerable.Range(0, Settings.SchemaProperties).ToDictionary(
                        property => "p_" + property,
                        _ => (object?)new D { ["type"] = "string", ["description"] = description })
                }
            }
        }).ToList();
        var request = new D { ["model"] = "public", ["messages"] = new List<object?>(), ["tools"] = tools };
        _ = ProtocolConverter.ConvertOwnedRequestWithMetadata(request, "chat", "chat", "upstream");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var converted = ProtocolConverter.ConvertOwnedRequestWithMetadata(request, "chat", "chat", "upstream").Request;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"Schema allocated={allocated}");
        Assert.True(allocated < Settings.MaximumSchemaAllocatedBytes,
            $"Schema sanitation allocated {allocated} bytes.");

        var originalFunction = (D)((D)tools[0]!)["function"]!;
        var convertedTools = (List<object?>)converted["tools"]!;
        var convertedFunction = (D)((D)convertedTools[0]!)["function"]!;
        var originalSchema = (D)originalFunction["parameters"]!;
        var convertedSchema = (D)convertedFunction["parameters"]!;
        Assert.NotSame(originalSchema, convertedSchema);
        Assert.DoesNotContain("required", originalSchema.Keys);
        Assert.Contains("required", convertedSchema.Keys);
    }

    [Fact]
    public void CommonParametersAreCopiedOnceAndRemainIsolated()
    {
        var items = Enumerable.Range(0, Settings.ParameterItems).Select(value => (object?)value).ToList();
        var request = new D
        {
            ["model"] = "public",
            ["messages"] = new List<object?>(),
            ["metadata"] = new D { ["items"] = items }
        };
        _ = ProtocolConverter.ConvertOwnedRequestWithMetadata(request, "chat", "responses", "upstream");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var converted = ProtocolConverter.ConvertOwnedRequestWithMetadata(request, "chat", "responses", "upstream").Request;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"Parameters allocated={allocated}");
        Assert.True(allocated < Settings.MaximumParameterAllocatedBytes,
            $"Parameter conversion allocated {allocated} bytes.");
        var output = (List<object?>)((D)converted["metadata"]!)["items"]!;
        output.Clear();
        Assert.Equal(Settings.ParameterItems, items.Count);
    }

    [Fact]
    public void ProductionResponseConversionDoesNotCopyUnusedExtensionGraph()
    {
        var response = new D
        {
            ["id"] = "c", ["model"] = "upstream", ["created"] = 1,
            ["choices"] = new List<object?>
            {
                new D { ["finish_reason"] = "stop", ["message"] = new D { ["content"] = "hello" } }
            },
            ["vendor_data"] = Enumerable.Range(0, Settings.ParameterItems).Select(value => (object?)value).ToList()
        };
        _ = ProtocolConverter.ConvertOwnedResponse(response, "responses", "chat", "public");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var converted = ProtocolConverter.ConvertOwnedResponse(response, "responses", "chat", "public");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"Production response allocated={allocated}");
        Assert.True(allocated < Settings.MaximumResponseAllocatedBytes,
            $"Response conversion allocated {allocated} bytes.");
        Assert.Equal("public", converted["model"]);
        Assert.Equal("upstream", response["model"]);
    }

    [Fact]
    public void CanonicalResponseExtensionsShareItsImmutableRawSnapshot()
    {
        var facade = new ProtocolConversionFacade();
        var response = new D
        {
            ["id"] = "c", ["model"] = "upstream", ["created"] = 1,
            ["choices"] = new List<object?>
            {
                new D { ["finish_reason"] = "stop", ["message"] = new D { ["content"] = "hello" } }
            }
        };
        var canonical = facade.ToCanonicalResponse(response, ProtocolKind.Chat);
        Assert.Equal(canonical.Raw!.Value.GetProperty("choices"), canonical.ExtensionData!["choices"]);
        var restored = facade.ConvertResponse(canonical, ProtocolKind.Chat, ProtocolKind.Chat);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(response), JsonSerializer.SerializeToElement(restored)));
    }

    [Fact]
    public void CanonicalResponseCapturesTheWireOnlyOnce()
    {
        var response = new D
        {
            ["id"] = "c", ["model"] = "upstream", ["created"] = 1,
            ["choices"] = new List<object?>
            {
                new D
                {
                    ["finish_reason"] = "stop",
                    ["message"] = new D { ["content"] = new string('x', Settings.TextCharacters) }
                }
            }
        };
        var facade = new ProtocolConversionFacade();
        _ = facade.ToCanonicalResponse(response, ProtocolKind.Chat);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var canonical = facade.ToCanonicalResponse(response, ProtocolKind.Chat);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"Canonical snapshot allocated={allocated}");
        Assert.True(allocated < Settings.MaximumCanonicalSnapshotAllocatedBytes,
            $"Canonical snapshot allocated {allocated} bytes.");
        Assert.Equal(Settings.TextCharacters, canonical.Text.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericJsonUsesBoundedBatchesInsteadOfOneWritePerLeaf(bool audit)
    {
        using var outputStream = new WriteProbe();
        using var writer = new Utf8JsonWriter(outputStream);
        var data = Enumerable.Range(0, Settings.NumericItems).Select(value => (object?)value).ToList();
        if (audit) ProxyAuditSpool.WriteJson(writer, data, 0);
        else OpenCodex.ProtocolConversion.Json.ProtocolJsonWriter.Write(writer, data, 0);
        writer.Flush();
        log.WriteLine($"Numeric JSON: audit={audit}, writes={outputStream.Writes}");
        Assert.True(outputStream.Writes <= Settings.MaximumJsonWrites, $"JSON used {outputStream.Writes} writes.");
    }

    [Fact]
    public void BatchedJsonPreservesScalarTypesAndNestedValues()
    {
        object?[] data =
        [
            null, true, false, (byte)1, (sbyte)-1, (short)-2, (ushort)2,
            int.MinValue, uint.MaxValue, long.MinValue, ulong.MaxValue,
            0.125f, 0.125d, 0.125m, "", "中\n\"😀",
            new D { ["values"] = new List<object?> { 1, null, "nested" } }
        ];
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            OpenCodex.ProtocolConversion.Json.ProtocolJsonWriter.Write(writer, data, 0);
        }
        using var actual = JsonDocument.Parse(output.ToArray());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(data), actual.RootElement));
    }

    private static async IAsyncEnumerable<SseEvent> TextEvents(
        int characters, int deltaCharacters, StreamResponseCapture? capture)
    {
        var first = new SseEvent("message_start", new D
        {
            ["type"] = "message_start",
            ["message"] = new D { ["id"] = "m", ["role"] = "assistant", ["content"] = new List<object?>() }
        });
        capture?.Accept(new SseFrame("", first));
        yield return first;
        var start = new SseEvent("content_block_start", new D
        {
            ["type"] = "content_block_start", ["index"] = 0,
            ["content_block"] = new D { ["type"] = "text", ["text"] = "" }
        });
        capture?.Accept(new SseFrame("", start));
        yield return start;
        var fragment = new string('x', deltaCharacters);
        for (var offset = 0; offset < characters; offset += deltaCharacters)
        {
            var item = new SseEvent("content_block_delta", new D
            {
                ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new D
                {
                    ["type"] = "text_delta",
                    ["text"] = offset + deltaCharacters <= characters ? fragment : fragment[..(characters - offset)]
                }
            });
            capture?.Accept(new SseFrame("", item));
            yield return item;
        }
        foreach (var item in new[]
        {
            new SseEvent("content_block_stop", new D { ["type"] = "content_block_stop", ["index"] = 0 }),
            new SseEvent("message_delta", new D { ["type"] = "message_delta", ["delta"] = new D { ["stop_reason"] = "end_turn" } }),
            new SseEvent("message_stop", new D { ["type"] = "message_stop" })
        })
        {
            capture?.Accept(new SseFrame("", item));
            yield return item;
        }
        await Task.CompletedTask;
    }

    private sealed class WriteProbe : Stream
    {
        internal int Writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Writes++;
        public override void Write(ReadOnlySpan<byte> buffer) => Writes++;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class Parameters
    {
        public required int GeneratedFrames { get; init; }
        public required int GeneratedDeltaCharacters { get; init; }
        public required long MaximumGeneratedFrameAllocatedBytes { get; init; }
        public required int TextCharacters { get; init; }
        public required int[] TextCharacterCounts { get; init; }
        public required int DeltaCharacters { get; init; }
        public required int MaximumTextAllocatedBytes { get; init; }
        public required int SchemaTools { get; init; }
        public required int SchemaProperties { get; init; }
        public required int DescriptionCharacters { get; init; }
        public required long MaximumSchemaAllocatedBytes { get; init; }
        public required int ParameterItems { get; init; }
        public required long MaximumParameterAllocatedBytes { get; init; }
        public required long MaximumResponseAllocatedBytes { get; init; }
        public required long MaximumCanonicalSnapshotAllocatedBytes { get; init; }
        public required int NumericItems { get; init; }
        public required int MaximumJsonWrites { get; init; }
        public required int SseReaderChunkCharacters { get; init; }
        public required long MaximumSseReaderAllocatedBytes { get; init; }
    }
}
