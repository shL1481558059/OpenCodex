# 流式输出集成测试文档

## 测试文件

`StreamingIntegrationTests.cs` - 完整的端到端集成测试，覆盖协议转换、WebSearch、ApplyPatch、Vision的所有场景。

## 测试覆盖范围

### 1. 协议转换基础测试

#### Chat → Responses
- `ChatToResponses_SimpleText_StreamsImmediatelyWithCorrectSequence`
  - 验证基础文本转换
  - TTFT < 100ms
  - 事件间隔 < 50ms
  - 事件序列完整正确

#### Messages → Responses  
- `MessagesToResponses_SimpleText_StreamsCorrectly`
  - Anthropic Messages API转换
  - 流式特性验证
  - 内容完整性验证

### 2. Vision 图像识别

- `MessagesToResponses_WithVision_PreservesImageContent`
  - 验证图像识别场景的token usage正确
  - 验证图像相关内容正确转换

### 3. ApplyPatch 工具转换

#### Freeform ApplyPatch
- `ApplyPatch_FreeformTool_StreamsAsCustomToolCall`
  - 验证原生 `apply_patch` 以 `custom_tool_call` 输出
  - 验证流式事件使用 `response.custom_tool_call_input.delta`
  - 验证最终 `input` 为完整 raw patch 文本

#### Update File
- `ApplyPatch_UpdateFile_PassesThroughAsFunctionCall`
  - 验证历史兼容工具 `apply_patch_update_file` 仍作为普通函数调用透传
  - 验证 arguments 保持原始 JSON
  - 验证不会改写成 `exec_command` / heredoc

#### Add File
- `ApplyPatch_AddFile_PassesThroughAsFunctionCall`
  - 验证 `apply_patch_add_file` 透传
  - 验证 `path` / `content` 原样保留

#### Batch Operations
- `ApplyPatch_Batch_PassesThroughAsFunctionCall`
  - 验证 `apply_patch_batch` 多操作透传
  - 验证 `operations[]` 原样保留

### 4. 流式性能验证

- `StreamingPerformance_NoBuffering_EventsYieldedImmediately`
  - 使用延迟模拟网络延迟
  - 验证事件逐个到达，不批量输出
  - 验证首个内容事件在 30ms 内到达

### 5. 事件完整性验证

- `EventCompleteness_NoMissingOrDuplicateEvents`
  - 验证每种事件类型出现次数正确
  - 验证事件顺序符合OpenAI Responses协议
  - 验证无丢失、无重复

## 关键验证指标

### 流式性能指标

```csharp
// TTFT (Time To First Token) - 首字时间
Assert.True(ttft < TimeSpan.FromMilliseconds(100));

// 事件间隔 - 验证无缓冲
for (int i = 1; i < timestamps.Count; i++)
{
    var interval = Stopwatch.GetElapsedTime(timestamps[i-1], timestamps[i]);
    Assert.True(interval < TimeSpan.FromMilliseconds(50));
}
```

### 事件序列验证

```csharp
// 必需事件
Assert.Equal("response.created", FindEvent(parsed, "response.created"));
Assert.Equal("response.output_item.added", FindEvent(parsed, "response.output_item.added"));
Assert.NotNull(FindEvent(parsed, "response.text.delta"));
Assert.NotNull(FindEvent(parsed, "response.done"));

// 顺序验证
var eventTypes = parsed.Select(e => e["_event_type"]).ToList();
Assert.Equal("response.created", eventTypes[0]);
Assert.Equal("response.done", eventTypes[^1]);
```

### 内容完整性验证

```csharp
// 拼接所有delta事件
var textDeltas = parsed
    .Where(e => "response.text.delta".Equals(e["_event_type"]))
    .Select(e => ((Dictionary<string, object?>)e["delta"]!)["text"]?.ToString() ?? "")
    .ToList();
    
Assert.Equal("Expected full text", string.Join("", textDeltas));
```

## 运行测试

### 前提条件

需要先修复 `ProxyImageFallbackTests.cs` 中的编译错误（删除 `ILocalImageOcrService` 引用）。

### 运行所有集成测试

```bash
dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj \
  --filter "FullyQualifiedName~StreamingIntegrationTests"
```

### 运行特定测试

```bash
# Chat转换测试
dotnet test --filter "FullyQualifiedName~ChatToResponses_SimpleText"

# ApplyPatch测试
dotnet test --filter "FullyQualifiedName~ApplyPatch"

# 性能测试
dotnet test --filter "FullyQualifiedName~StreamingPerformance"
```

## 测试辅助工具

### CollectWithTimestamps

收集事件的同时记录时间戳，用于性能分析：

```csharp
var (events, ttft, timestamps) = await CollectWithTimestamps(streamable);
```

### ParseEvents

将SSE行解析成结构化事件列表：

```csharp
var parsed = ParseEvents(sseLines);
var completed = FindEvent(parsed, "response.done");
```

### SseBlock / MessagesBlock

构建测试用的SSE事件块：

```csharp
SseBlock(ChatChunk(content: "Hello"), eventName: "chunk")
MessagesBlock("content_block_delta", new { type = "text_delta", text = "Hi" })
```

## WebSearchSimulator集成测试（待添加）

当前测试未包含WebSearchSimulator，因为它需要：
- Mock IUpstreamClient
- Mock IWebSearchClient
- 复杂的多轮对话模拟

建议单独创建 `WebSearchSimulatorIntegrationTests.cs`，测试：

1. 无web_search调用时的流式特性
2. 单次web_search调用的完整流程
3. 多次web_search调用的流式保持
4. completed事件的延迟处理

## 持续集成建议

1. **性能回归检测**
   - TTFT基准：100ms
   - 事件间隔基准：50ms
   - 超过基准时CI失败

2. **协议兼容性检测**
   - 每次修改SseStreamConverter后必须运行全部测试
   - 事件序列变更需要更新测试

3. **真实场景录制**
   - 建议录制真实的上游SSE响应作为测试数据
   - 放在 `TestData/` 目录下
   - 定期更新以匹配上游API变化

## 故障排查

### TTFT过高

如果 `TTFT > 100ms`，检查：
1. 是否在`await foreach`前有同步等待？
2. 是否先收集到List再yield？
3. WebSearchSimulator是否有缓冲逻辑？

### 事件丢失

如果事件数量不对，检查：
1. `yield return` 是否在正确的位置？
2. 是否有条件跳过了某些事件？
3. `response.completed` 是否被特殊处理？

### 格式错误

如果ApplyPatch格式不对，检查：
1. `ProtocolConverter.ApplyPatchTools.cs` 的转换逻辑
2. `ApplyPatchJsonDeltaDecoder.cs` 是否正确处理 JSON 字符串增量和转义
3. `SseStreamConverter.Chat.cs` / `SseStreamConverter.Messages.cs` 是否对 apply_patch 发送了 `custom_tool_call_input.delta`

## 相关文件

- `SseStreamConverter.cs` - 核心转换逻辑
- `SseStreamConverter.Messages.cs` - Messages协议转换
- `ProtocolConverter.ApplyPatchTools.cs` - ApplyPatch工具转换
- `WebSearchSimulator.Streaming.cs` - WebSearch流式处理
- `SseStreamConverterTests.cs` - 现有的单元测试
